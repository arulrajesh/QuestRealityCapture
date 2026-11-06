#if UNITY_EDITOR
#nullable enable

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RealityLog.EditorTools
{
    /// <summary>
    /// Headless Android build for the live-streaming capture app.
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;repo&gt; -buildTarget Android
    ///   -executeMethod RealityLog.EditorTools.CommandLineBuild.BuildAndroid
    ///   -logFile Builds/build.log [-outputPath Builds/QuestRealityCapture-live.apk]
    /// </code>
    /// See Tools/build_and_install.ps1.
    /// </summary>
    public static class CommandLineBuild
    {
        public static void BuildAndroid()
        {
            var output = GetArg("-outputPath") ?? Path.Combine("Builds", "QuestRealityCapture-live.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
            var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[CommandLineBuild] No enabled scenes in Build Settings.");
                EditorApplication.Exit(2);
                return;
            }

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.buildAppBundle = false;  // an APK installs directly with adb
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            var summary = report.summary;
            Debug.Log($"[CommandLineBuild] {summary.result}: {summary.totalErrors} errors, {summary.totalSize / (1024 * 1024)} MB -> {output}");
            if (summary.result != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }

        private static string? GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
#endif
