using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MORLA.XR.Editor
{
    /// <summary>Deterministically prepares the bundled Chinese font and reusable workspace entry prefab.</summary>
    public sealed class MorlaWorkspaceAssets : IPreprocessBuildWithReport
    {
        public int callbackOrder => -100;
        [InitializeOnLoadMethod]
        private static void OnLoad() => EditorApplication.delayCall += EnsureAssets;
        [MenuItem("MORLA/Prepare Workspace Assets")]
        public static void EnsureAssets()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorApplication.delayCall += EnsureAssets; return; }
            const string fontPath = "Assets/Resources/MorlaFont.asset";
            Directory.CreateDirectory("Assets/Resources");
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath) == null)
            {
                var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/UI/Fonts/NotoSansSC-Regular.ttf");
                if (source == null) { Debug.LogError("[MORLA] Bundled Noto Sans SC font is missing."); return; }
                var font = TMP_FontAsset.CreateFontAsset(source, 48, 6, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
                font.name = "MORLA Noto Sans SC";
                AssetDatabase.CreateAsset(font, fontPath);
                font.material.name = "MORLA Font Material"; AssetDatabase.AddObjectToAsset(font.material, font);
                foreach (var texture in font.atlasTextures)
                { texture.name = "MORLA Font Atlas"; AssetDatabase.AddObjectToAsset(texture, font); }
                font.TryAddCharacters("MORLA XR 0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ开始暂停发送连接追踪设置视频显示语音主机手柄手部全身左侧右侧概览关闭应用保存当前等待有效状态权限机器人恢复确认异常未检测麦克风诊断关于配置工作区返回" +
                    " ·：，。/+-×%()[]<>!?_");
                EditorUtility.SetDirty(font); AssetDatabase.SaveAssets();
            }
            const string prefabPath = "Assets/Resources/MorlaWorkspace.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                var root = new GameObject("MORLA Workspace");
                root.AddComponent<WorkspaceUIController>(); root.AddComponent<WorkspacePointer>();
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath); Object.DestroyImmediate(root);
            }
        }
        public void OnPreprocessBuild(BuildReport report)
        {
            EnsureAssets();
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resources/MorlaFont.asset") == null)
                throw new BuildFailedException("MORLA Chinese font was not prepared. Use MORLA > Prepare Workspace Assets.");
        }
        [MenuItem("MORLA/Build Quest APK")]
        public static void BuildQuest()
        {
            EnsureAssets();
            string output = "Builds/MORLA-XR.apk"; Directory.CreateDirectory("Builds");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Scene.unity" }, locationPathName = output,
                target = BuildTarget.Android, options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("MORLA Quest APK build failed");
            Debug.Log("[MORLA] APK built: " + Path.GetFullPath(output));
        }
    }
}
