using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
#if VRC_LIGHT_VOLUMES_3
using UnityEngine.SceneManagement;
using VRCLightVolumes.Editor;
#endif

namespace Glim
{
    [Serializable]
    public class BakeReport
    {
        public double bakeTime;
        public string finishedAt;
        public int lightmapCount;
        public long lightmapBytes;
        public long lightmapMemoryBytes;
        public long lightingDataBytes;
        public int probeCount;
    }

    public class Bake
    {
        static List<Bindings.SHProbe> _bakeProbesResults = new();
        static volatile bool _isComplete = false;
        static volatile bool _running = false;
        static int _progressID = -1;
        static int _ambientProbeIndex = 0;

        static BakeContext _context = null;

        static volatile float _progress = 0f;
        static volatile string _bakeMessage = "";
        public static StringBuilder bakeMessages = new();
        static volatile bool _isPreview = false;
        static volatile bool _cancelRequested = false;

        public static bool IsBaking => _running && !_isPreview;
        public static bool IsCancelling => _cancelRequested && _running;
        public static float BakeProgress => _progress;
        public static string BakeMessage => _bakeMessage;

        public static void Cancel()
        {
            if (!_running || _isPreview || _cancelRequested)
            {
                return;
            }
            _cancelRequested = true;
            Bindings.app_request_cancel();
        }

        [AOT.MonoPInvokeCallback(typeof(Bindings.ReadbackProbesCallback))]
        public static void OnReadbackLightprobes(Bindings.LightprobesReadbackData data)
        {
            bakeMessages.AppendLine($"Received Probes {data.probes_count}");
            var probes = data.GetProbes();

            _bakeProbesResults.AddRange(probes);
        }

        [AOT.MonoPInvokeCallback(typeof(Bindings.LogCallback))]
        public static void OnLogCalback(Bindings.LogData data)
        {
            if (data.ty == 2) // progress
            {
                _progress = data.progress;
            }

            if (data.ty == 0) // info
            {
                _bakeMessage = data.message.ToString();
                bakeMessages.AppendLine(_bakeMessage);
                ChangeProgressMessage(_bakeMessage);
            }
            if (data.ty == 1) // error
            {
                throw new Exception(data.message.ToString());
            }
        }

        static double _bakeStartTime = 0.0;

        static readonly MethodInfo StorageMemorySize = ResolveStorageMemorySize();

        static MethodInfo ResolveStorageMemorySize()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.TextureUtil", false))
                .FirstOrDefault(t => t != null);

            return type?.GetMethod("GetStorageMemorySizeLong",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        static long GetCompressedTextureBytes(Texture2D texture)
        {
            if (texture == null || StorageMemorySize == null)
            {
                return 0;
            }

            return (long)StorageMemorySize.Invoke(null, new object[] { texture });
        }

        static string BakeReportPath(string scenePath)
        {
            var dir = Path.GetDirectoryName(scenePath);
            var sceneName = Path.GetFileNameWithoutExtension(scenePath);
            return Path.Combine(dir, sceneName, "bakeReport.json");
        }

        public static BakeReport LoadReport(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return null;
            }

            var path = BakeReportPath(scenePath);
            return File.Exists(path) ? JsonUtility.FromJson<BakeReport>(File.ReadAllText(path)) : null;
        }

        public static int ReportVersion { get; private set; }

        static void SaveReport(string scenePath, BakeReport report)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return;
            }

            var path = BakeReportPath(scenePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report));
            ReportVersion++;
        }

        static void ChangeProgressMessage(string title)
        {
            if (_progressID != -1)
            {
                Progress.Finish(_progressID, Progress.Status.Succeeded);
                _progressID = Progress.Start(title, null, Progress.Options.None);
                Progress.RegisterCancelCallback(_progressID, () => { Cancel(); return true; });
            }

            Progress.Report(_progressID, _progress, _bakeMessage);
        }

        static void PollBakeComplete()
        {
            if (!_isComplete)
            {
                if (_progressID != -1)
                {
                    Progress.Report(_progressID, _progress, _bakeMessage);
                }
                return;
            }

            if (_context.isPreview)
            {
                PrintBakeLogs();
                ResetBake();
                return;
            }

            if (_cancelRequested)
            {
                if (_progressID != -1)
                {
                    Progress.Finish(_progressID, Progress.Status.Canceled);
                    _progressID = -1;
                }
                ResetBake();
                return;
            }
            try
            {
                var now = Time.realtimeSinceStartupAsDouble;

                bakeMessages.AppendLine($"Bake Complete in {now - _bakeStartTime}");

                List<LightmapData> lightmapDatas = new();
                for (int i = 0; i < _context.groups.Count; i++)
                {
                    var lmData = new LightmapData
                    {
                        lightmapColor = null,
                        lightmapDir = null,
                        shadowMask = null
                    };
                    lightmapDatas.Add(lmData);
                }

                var scenePath = _context.scene.path;
                string sceneName = _context.scene.name;

                long lightmapBytes = 0;
                long lightmapMemoryBytes = 0;

                bool hasDirectional = _context.lightmapMode == LightmapMode.DominantDirection || _context.lightmapMode == LightmapMode.CombinedSH;

                // hard coded paths for now in rust
                for (int groupIndex = 0; groupIndex < _context.groups.Count; groupIndex++)
                {
                    BakeContextGroup group = _context.groups[groupIndex];

                    var diffuseName = $"Lightmap-{groupIndex}_Diffuse.exr";
                    var directionalName = $"Lightmap-{groupIndex}_Directional.tga";

                    {
                        string metaPath = Path.Combine(_context.outputDir, $"{diffuseName}.meta");
                        if (!File.Exists(metaPath))
                        {
                            var guid = GUID.Generate().ToString();
                            var yaml = CreateTextureImporterMeta(guid, false);
                            File.WriteAllText(metaPath, yaml);
                        }
                    }

                    if (hasDirectional)
                    {
                        string metaPath = Path.Combine(_context.outputDir, $"{directionalName}.meta");
                        if (!File.Exists(metaPath))
                        {
                            var guid = GUID.Generate().ToString();
                            var yaml = CreateTextureImporterMeta(guid, true);
                            File.WriteAllText(metaPath, yaml);
                        }
                    }
                }

                AssetDatabase.Refresh();

                for (int groupIndex = 0; groupIndex < _context.groups.Count; groupIndex++)
                {
                    BakeContextGroup group = _context.groups[groupIndex];

                    var groupAsset = _context.groups[groupIndex].groupAsset;
                    var diffuseName = $"Lightmap-{groupIndex}_Diffuse.exr";
                    var directionalName = $"Lightmap-{groupIndex}_Directional.tga";

                    {
                        var path = Path.Combine(_context.outputDir, diffuseName);
                        var loadedAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                        lightmapMemoryBytes += GetCompressedTextureBytes(loadedAsset);
                        lightmapDatas[groupIndex].lightmapColor = loadedAsset;
                        lightmapBytes += new FileInfo(path).Length;
                    }

                    if (hasDirectional)
                    {
                        var path = Path.Combine(_context.outputDir, directionalName);
                        var loadedAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                        lightmapMemoryBytes += GetCompressedTextureBytes(loadedAsset);
                        lightmapDatas[groupIndex].lightmapDir = loadedAsset;
                        lightmapBytes += new FileInfo(path).Length;
                    }
                }

                // EditorUtility.SetDirty(_context.baker);

                using var lda = new SerializedObject(_context.storage);
                LightingData.InspectorModeObject.SetValue(lda, InspectorMode.DebugInternal);

                Debug.Assert(_context.storage != null);

                var lightmapsProp = lda.FindProperty("m_Lightmaps");
                Debug.Assert(lightmapsProp != null);

                lightmapsProp.arraySize = lightmapDatas.Count;
                for (int i = 0; i < lightmapDatas.Count; i++)
                {
                    var element = lightmapsProp.GetArrayElementAtIndex(i);

                    element.FindPropertyRelative("m_Lightmap").objectReferenceValue = lightmapDatas[i].lightmapColor;
                    element.FindPropertyRelative("m_DirLightmap").objectReferenceValue = lightmapDatas[i].lightmapDir;
                    element.FindPropertyRelative("m_ShadowMask").objectReferenceValue = lightmapDatas[i].shadowMask;
                }

                lda.FindProperty("m_LightmapsMode").intValue = hasDirectional ?
                    (int)LightmapsMode.CombinedDirectional : (int)LightmapsMode.NonDirectional;

                // apply light probes
                var lightProbesRef = lda.FindProperty("m_LightProbes").objectReferenceValue;
                SphericalHarmonicsL2 sh = new();
                var obj = lightProbesRef as LightProbes;
                Debug.Assert(obj != null);
                if (obj != null && _bakeProbesResults.Count > 0)
                {
                    SphericalHarmonicsL2[] bakedProbesArray = obj.bakedProbes;
                    int bakedCoeffCount = bakedProbesArray.Length;

                    for (int i = 0; i < bakedCoeffCount; i++)
                    {
                        Bindings.SHProbe probeData = _bakeProbesResults[i];

                        sh[0, 0] = probeData.L0.x; sh[0, 1] = probeData.L1_1.x; sh[0, 2] = probeData.L10.x; sh[0, 3] = probeData.L11.x; sh[0, 4] = probeData.L2_2.x; sh[0, 5] = probeData.L2_1.x; sh[0, 6] = probeData.L20.x; sh[0, 7] = probeData.L21.x; sh[0, 8] = probeData.L22.x;
                        sh[1, 0] = probeData.L0.y; sh[1, 1] = probeData.L1_1.y; sh[1, 2] = probeData.L10.y; sh[1, 3] = probeData.L11.y; sh[1, 4] = probeData.L2_2.y; sh[1, 5] = probeData.L2_1.y; sh[1, 6] = probeData.L20.y; sh[1, 7] = probeData.L21.y; sh[1, 8] = probeData.L22.y;
                        sh[2, 0] = probeData.L0.z; sh[2, 1] = probeData.L1_1.z; sh[2, 2] = probeData.L10.z; sh[2, 3] = probeData.L11.z; sh[2, 4] = probeData.L2_2.z; sh[2, 5] = probeData.L2_1.z; sh[2, 6] = probeData.L20.z; sh[2, 7] = probeData.L21.z; sh[2, 8] = probeData.L22.z;

                        bakedProbesArray[i] = sh;
                    }

                    obj.bakedProbes = bakedProbesArray;
                    EditorUtility.SetDirty(obj);
                }

                // skybox probe
                {
                    var ambientProbe = _bakeProbesResults[_ambientProbeIndex];
                    sh[0, 0] = ambientProbe.L0.x; sh[0, 1] = ambientProbe.L1_1.x; sh[0, 2] = ambientProbe.L10.x; sh[0, 3] = ambientProbe.L11.x; sh[0, 4] = ambientProbe.L2_2.x; sh[0, 5] = ambientProbe.L2_1.x; sh[0, 6] = ambientProbe.L20.x; sh[0, 7] = ambientProbe.L21.x; sh[0, 8] = ambientProbe.L22.x;
                    sh[1, 0] = ambientProbe.L0.y; sh[1, 1] = ambientProbe.L1_1.y; sh[1, 2] = ambientProbe.L10.y; sh[1, 3] = ambientProbe.L11.y; sh[1, 4] = ambientProbe.L2_2.y; sh[1, 5] = ambientProbe.L2_1.y; sh[1, 6] = ambientProbe.L20.y; sh[1, 7] = ambientProbe.L21.y; sh[1, 8] = ambientProbe.L22.y;
                    sh[2, 0] = ambientProbe.L0.z; sh[2, 1] = ambientProbe.L1_1.z; sh[2, 2] = ambientProbe.L10.z; sh[2, 3] = ambientProbe.L11.z; sh[2, 4] = ambientProbe.L2_2.z; sh[2, 5] = ambientProbe.L2_1.z; sh[2, 6] = ambientProbe.L20.z; sh[2, 7] = ambientProbe.L21.z; sh[2, 8] = ambientProbe.L22.z;
                    var ambientProbeProp = lda.FindProperty("m_BakedAmbientProbeInLinear");
                    var child = ambientProbeProp.Copy();
                    var end = child.GetEndProperty();

                    int coefficient = 0;

                    if (child.Next(true))
                    {
                        do
                        {
                            if (child.propertyType == SerializedPropertyType.Float)
                            {
                                child.floatValue = sh[coefficient / 9, coefficient % 9];
                                coefficient++;
                            }
                        }
                        while (child.Next(false) &&
                               !SerializedProperty.EqualContents(child, end));
                    }
                }

                lda.ApplyModifiedPropertiesWithoutUndo();
                string ldaName = "LightingData";

                // move
                string destPath = Path.Combine(_context.outputDir, $"{ldaName}.asset").Replace("\\", "/");
                if (AssetDatabase.LoadMainAssetAtPath(destPath) != null)
                {
                    AssetDatabase.DeleteAsset(destPath);
                }
                AssetDatabase.MoveAsset(LightingData.TempLightingDataPath, destPath);

#if VRC_LIGHT_VOLUMES_3
                SaveLightVolumeProbes(_context);
#endif


                // apply new asset
                var newLda = AssetDatabase.LoadAssetAtPath<LightingDataAsset>(destPath);
                using var lda2 = new SerializedObject(newLda);


                lda2.FindProperty("m_Name").stringValue = ldaName;
                lda2.ApplyModifiedPropertiesWithoutUndo();
                Lightmapping.lightingDataAsset = newLda;



                EditorSceneManager.MarkSceneDirty(_context.scene);

                LightmapSettings.lightmaps = lightmapDatas.ToArray();
                LightmapSettings.lightmapsMode = hasDirectional ? LightmapsMode.CombinedDirectional : LightmapsMode.NonDirectional;

                SaveReport(scenePath, new BakeReport
                {
                    bakeTime = now - _bakeStartTime,
                    finishedAt = DateTime.Now.ToString("o"),
                    lightmapCount = lightmapDatas.Count,
                    lightmapBytes = lightmapBytes,
                    lightmapMemoryBytes = lightmapMemoryBytes,
                    lightingDataBytes = new FileInfo(destPath).Length,
                    probeCount = _bakeProbesResults.Count,
                });

#if XATLAS_LIGHTMAP_INCLUDED
                var packers = GameObject.FindObjectsOfType<z3y.XatlasLightmapPacker>();
                foreach (var packer in packers)
                {
                    packer.OnValidate();
                }
#endif

                if (_context.bakeReflectionProbes)
                {
                    GlimLightmapperEditor.BakeAllReflectionProbesSnapshots(_context.scene, _context.reflectionProbesSuperSampling ? 2 : 1, _context.reflectionProbesSpecular);
                }
            }
            finally
            {
                PrintBakeLogs();
                ResetBake();
            }
        }

        private static void PrintBakeLogs()
        {
            string logs = bakeMessages.ToString();
            if (!string.IsNullOrEmpty(logs))
            {
                Debug.Log("Bake Logs:\n" + logs);
            }
        }

        static void ResetBake()
        {
            EditorApplication.update -= PollBakeComplete;
            _bakeProbesResults = new();
            _isComplete = false;
            _running = false;
            _context = null;
            _progress = 0f;
            _bakeMessage = "";
            _isPreview = false;
            _cancelRequested = false;
            if (_progressID != -1)
            {
                Progress.Finish(_progressID, Progress.Status.Succeeded);
            }
            _progressID = -1;
            bakeMessages.Clear();
        }

#if VRC_LIGHT_VOLUMES_3
        // VRCLV only exposes custom-lightmapper probes through its primary Manager; non-primary Managers report zero volumes.
        static VRCLightVolumes.LightVolumeManager FindLightVolumeManager(Scene scene)
        {
            return scene.GetRootGameObjects()
                .SelectMany(x => x.GetComponentsInChildren<VRCLightVolumes.LightVolumeManager>(true))
                .FirstOrDefault(m => m.Editor.GetCustomProbesCount() > 0);
        }

        static void AddLightProbeVolumes(BakeContext ctx)
        {
            var manager = FindLightVolumeManager(ctx.scene);
            if (manager == null)
            {
                return;
            }

            // Mirrors VRCLV's custom probe ID order (LightVolumeManagerEditorBackend.IsCustomProbeVolume) so each ID resolves to its volume.
            var volumes = manager.LightVolumeInstances
                .Where(v => v != null && v.Bake && v.gameObject.activeInHierarchy && !v.CompareTag("EditorOnly"))
                .ToArray();

            int volumeCount = manager.Editor.GetCustomProbesCount();
            if (volumes.Length != volumeCount)
            {
                Debug.LogError($"Light Volume ID mismatch: VRCLV reports {volumeCount} bakeable volumes, found {volumes.Length}. Light volumes were skipped");
                return;
            }

            for (int id = 0; id < volumeCount; id++)
            {
                // GetCustomProbes recalculates the volume resolution, so read it afterwards.
                Vector3[] positions = manager.Editor.GetCustomProbes(id);
                var volume = volumes[id];
                Vector3Int resolution = volume.Resolution;
                if (positions.Length == 0 || positions.Length != resolution.x * resolution.y * resolution.z)
                {
                    Debug.LogError($"Light Volume {volume.name} probe count does not match its resolution, skipped", volume);
                    continue;
                }

                // Half of the smallest world-space voxel dimension, including axes with a single voxel.
                Vector3 scale = VRCLightVolumes.LightVolumeTools.GetScale(volume);
                float radius = Mathf.Min(
                    Mathf.Abs(scale.x) / resolution.x,
                    Mathf.Abs(scale.y) / resolution.y,
                    Mathf.Abs(scale.z) / resolution.z) * 0.5f;

                ctx.probeVolumes.Add(new LightProbeVolumeData
                {
                    id = id,
                    indexStart = ctx.probePositions.Count,
                    probeCount = positions.Length,
                });

                foreach (var position in positions)
                {
                    Vector4 probe = position;
                    probe.w = radius;
                    ctx.probePositions.Add(probe);
                }
            }
        }

        static void SaveLightVolumeProbes(BakeContext ctx)
        {
            if (ctx.probeVolumes.Count == 0)
            {
                return;
            }

            var manager = FindLightVolumeManager(ctx.scene);
            if (manager == null)
            {
                Debug.LogError("Light Volume Manager is missing, baked light volumes were discarded");
                return;
            }

            foreach (var data in ctx.probeVolumes)
            {
                var l0 = new Vector3[data.probeCount];
                var l1r = new Vector3[data.probeCount];
                var l1g = new Vector3[data.probeCount];
                var l1b = new Vector3[data.probeCount];

                for (int i = 0; i < data.probeCount; i++)
                {
                    var probe = _bakeProbesResults[data.indexStart + i];

                    // VRCLV expects L1 per color channel ordered as (x, y, z) = (L11, L1-1, L10), matching Unity's SH layout.
                    l0[i] = probe.L0;
                    l1r[i] = new Vector3(probe.L11.x, probe.L1_1.x, probe.L10.x);
                    l1g[i] = new Vector3(probe.L11.y, probe.L1_1.y, probe.L10.y);
                    l1b[i] = new Vector3(probe.L11.z, probe.L1_1.z, probe.L10.z);
                }

                // VRCLV writes the textures, then regenerates its atlas once every volume is stored.
                manager.Editor.SetCustomProbesBaked(data.id, l0, l1r, l1g, l1b);
            }
        }
#endif

        // Refocus the window for QoL
        static void RestoreSelection()
        {
            var baker = UnityEngine.Object.FindAnyObjectByType<GlimLightmapper>();

            if (baker != null)
            {
                Selection.activeGameObject = baker.gameObject;
            }
        }

        public static void Start(GlimLightmapper baker, Bindings.GlimConfig config)
        {
            if (_running)
            {
                Debug.LogError("Bake or preview already running");
                return;
            }

            ResetBake();

            EditorApplication.update += PollBakeComplete;

            var ctx = new BakeContext(baker, config);

            // Ambient Light Probe
            _ambientProbeIndex = ctx.probePositions.Count;
            ctx.probePositions.Add(new Vector4(10000.0f, 10000.0f, 10000.0f, 0.0f));

#if VRC_LIGHT_VOLUMES_3
            AddLightProbeVolumes(ctx);
#endif

            _context = ctx;

            _running = true;
            _isPreview = config.is_preview;

            if (!config.is_preview)
            {
                _progressID = Progress.Start("", null, Progress.Options.None);
                Progress.RegisterCancelCallback(_progressID, () => { Cancel(); return true; });
                RestoreSelection();
            }

            _bakeStartTime = Time.realtimeSinceStartupAsDouble;
            var thread = new Thread(() =>
            {
                try
                {
                    var output_dir = Bindings.FfiString.FromString(ctx.outputDir);
                    var app = Bindings.app_new(config, output_dir);

                    if (app == null)
                    {
                        throw new Exception("failed to launch");
                    }


                    for (int i = 0; i < ctx.sceneMesh.Count; i++)
                    {
                        var data = ctx.sceneMesh[i];

                        unsafe
                        {
                            fixed (Vector3* vPtr = data.vertices)
                            fixed (Vector3* nPtr = data.normals)
                            fixed (Vector2* uPtr = data.uvs)
                            fixed (int* iPtr = data.indices)
                            {
                                var exportedMesh = new Bindings.Mesh
                                {
                                    vertices = vPtr,
                                    normals = nPtr,
                                    uvs = uPtr,
                                    indices = (uint*)iPtr,
                                    vertices_length = (uint)data.vertices.Length,
                                    indices_length = (uint)data.indices.Length,
                                    lightmap_group = data.groupIndex,
                                    backface_gi = data.backfaceGI,
                                    transparent = data.transparent,
                                    emissive = data.emissive,
                                };

                                Bindings.app_add_mesh(app, exportedMesh);
                            }
                        }
                    }
                    // free
                    ctx.sceneMesh = new();

                    foreach (var light in ctx.sceneLights)
                    {
                        Bindings.app_add_light(app, light);
                    }

                    foreach (var group in ctx.groups)
                    {
                        unsafe
                        {
                            fixed (Color32* albedoPtr = group.albedo)
                            fixed (byte* emissionsPtr = group.emission)
                            {
                                Bindings.app_add_lightmap_group(
                                    app,
                                    group.settings,
                                    (byte*)albedoPtr,
                                    (uint)(group.albedo.Length * 4),
                                    emissionsPtr,
                                    (uint)group.emission.Length
                                );
                            }
                        }
                        group.ClearPixels();
                    }

                    foreach (var position in ctx.probePositions)
                    {
                        Vector3 p = (Vector3)position;
                        float r = position.w;
                        Bindings.app_add_probe(app, p, r);
                    }

                    uint size = (uint)SkyboxCapture.RESOLUTION;
                    Bindings.app_set_skybox(app, _context.skyboxPixels, (uint)_context.skyboxPixels.Length * 4, size, size);

                    Bindings.app_run(app);

                    Bindings.app_destroy(app);
                    _running = false;
                    _isComplete = true;
                }
                catch (Exception e)
                {
                    _running = false;
                    _isComplete = true;
                    PrintBakeLogs();
                    Debug.LogException(e);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();


        }

        public static string CreateTextureImporterMeta(string guid, bool directional)
        {
            int alphaUsage = directional ? 1 : 0;
            int textureType = directional ? 12 : 6;
            string yaml = $@"
fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 0
    linearTexture: 1
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 0
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 0
  nPOTScale: 1
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: {alphaUsage}
  alphaIsTransparency: 0
  spriteTessellationDetail: -1
  textureType: {textureType}
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 8192
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 2
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 1
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
";
            return yaml;
        }
    }
}
