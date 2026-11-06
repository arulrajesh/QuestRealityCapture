// Minimal stand-ins for the Unity APIs used by Assets/RealityLog/Scripts/Runtime/Streaming.
// Compile-check only (plus JSON/wire behavior via System.Text.Json); NOT a Unity runtime.
#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Unity.Collections
{
    public enum Allocator { Temp, Persistent }
    public enum NativeArrayOptions { ClearMemory, UninitializedMemory }

    public struct NativeArray<T> : IDisposable where T : struct
    {
        private T[]? data;
        public NativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory) { data = new T[length]; }
        public NativeArray(T[] source, Allocator allocator) { data = (T[])source.Clone(); }
        public bool IsCreated => data != null;
        public int Length => data?.Length ?? 0;
        public T this[int i] { get => data![i]; set => data![i] = value; }
        public T[] ToArray() => (T[])data!.Clone();
        public void Dispose() { data = null; }
        public static void Copy(NativeArray<T> src, NativeArray<T> dst, int length) { Array.Copy(src.data!, dst.data!, length); }
    }
}

namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat { R8G8B8A8_UNorm, R8G8B8A8_SRGB }
}

namespace UnityEngine.Rendering
{
    public enum IndexFormat { UInt16, UInt32 }
}

namespace UnityEngine
{
    using Unity.Collections;
    using UnityEngine.Experimental.Rendering;
    using UnityEngine.Rendering;

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(float s, Vector3 v) => new(s * v.x, s * v.y, s * v.z);
        public static float Distance(Vector3 a, Vector3 b) => (float)Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new(0, 0, 0, 1);
        public static float Angle(Quaternion a, Quaternion b)
        {
            var dot = Math.Min(1.0, Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w));
            return (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color Lerp(Color x, Color y, float t) => new(x.r + (y.r - x.r) * t, x.g + (y.g - x.g) * t, x.b + (y.b - x.b) * t, x.a + (y.a - x.a) * t);
        public static implicit operator Color32(Color c) => new((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), (byte)(c.a * 255));
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public static class Mathf
    {
        public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int CeilToInt(float v) => (int)Math.Ceiling(v);
    }

    public static class Debug
    {
        public static void Log(object m) => Console.WriteLine(m);
        public static void LogWarning(object m) => Console.WriteLine("WARN " + m);
        public static void LogError(object m) => Console.WriteLine("ERROR " + m);
    }

    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
        public static string ToJson(object o) => JsonSerializer.Serialize(o, o.GetType(), Options);
    }

    public static class Application
    {
        public static string persistentDataPath => System.IO.Path.GetTempPath();
        public static string version => "test";
    }

    public static class SystemInfo { public static string deviceModel => "stub"; }
    public static class Time { public static float time => 0f; }

    public static class ImageConversion
    {
        public static NativeArray<byte> EncodeNativeArrayToJPG<T>(NativeArray<T> input, GraphicsFormat format, uint width, uint height, uint rowBytes = 0, int quality = 75) where T : struct
            => new(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, Allocator.Persistent);
    }

    public class Object
    {
        public string name = string.Empty;
        public static void Destroy(Object o) { }
        public static bool operator ==(Object? a, Object? b) => ReferenceEquals(a, b);
        public static bool operator !=(Object? a, Object? b) => !ReferenceEquals(a, b);
        public override bool Equals(object? o) => ReferenceEquals(this, o);
        public override int GetHashCode() => base.GetHashCode();
    }

    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }

    public class Behaviour : Component { public bool enabled = true; }
    public class MonoBehaviour : Behaviour { }

    public class Transform : Component
    {
        public Vector3 localPosition;
        public void SetParent(Transform parent, bool worldPositionStays) { }
        public void SetPositionAndRotation(Vector3 p, Quaternion r) { }
    }

    public class GameObject : Object
    {
        private readonly List<Component> components = new();
        public Transform transform;
        public GameObject(string name = "") { this.name = name; transform = new Transform { gameObject = this }; }
        public T AddComponent<T>() where T : Component, new() { var c = new T { gameObject = this }; components.Add(c); return c; }
        public T GetComponent<T>() where T : Component { foreach (var c in components) if (c is T t) return t; return null!; }
        public void SetActive(bool value) { }
    }

    public class Shader : Object { }

    public class Material : Object
    {
        public Material(Shader shader) { }
        public void SetFloat(string name, float value) { }
    }

    public class Font : Object { public Material material = new(new Shader()); }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null!;
        public static T GetBuiltinResource<T>(string path) where T : Object, new() => new T();
    }

    public class Mesh : Object
    {
        public IndexFormat indexFormat;
        public void MarkDynamic() { }
        public void Clear() { }
        public void SetVertices(Vector3[] v) { }
        public void SetColors(Color32[] c) { }
        public void SetTriangles(int[] t, int submesh, bool calculateBounds = true) { }
    }

    public class MeshFilter : Component { public Mesh sharedMesh = null!; }
    public class MeshRenderer : Component { public Material sharedMaterial = null!; }

    public class LineRenderer : Component
    {
        public Material sharedMaterial = null!;
        public float widthMultiplier;
        public bool useWorldSpace;
        public int positionCount;
        public Color startColor, endColor;
        public void SetPositions(Vector3[] p) { }
    }

    public enum TextAnchor { MiddleCenter }
    public enum TextAlignment { Center }

    public class TextMesh : Component
    {
        public Font font = null!;
        public int fontSize;
        public float characterSize;
        public TextAnchor anchor;
        public TextAlignment alignment;
        public bool richText;
        public string text = string.Empty;
    }

    public class Camera : Behaviour { public static Camera? main => null; }
}

namespace RealityLog
{
    public static class Constants { public const string LOG_TAG = "RealityLog"; }
}
