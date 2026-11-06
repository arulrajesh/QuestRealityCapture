# StreamingCheck

This harness checks the headset streaming code without a Unity install:

* it compiles `Assets/RealityLog/Scripts/Runtime/Streaming/*.cs` as **C# 9** against minimal stand-in Unity types (`UnityStubs.cs`);
* it cross-checks the wire format with the Python receiver in [metaquest-3d-reconstruction](https://github.com/arulrajesh/metaquest-3d-reconstruction) (`mq3drecon.streaming`).

It is not a Unity runtime. The recorder hooks, the shader, and on-device behavior still need a real Unity build.

```bash
dotnet build
dotnet run --no-build -- encode from_csharp.bin
python -I cross_check.py decode-csharp from_csharp.bin      # Python decodes C#-encoded messages
python -I cross_check.py encode-feedback from_python.bin    # Python encodes laptop feedback
dotnet run --no-build -- decode from_python.bin             # C# decodes it
```

Run the Python steps with the mq3drecon virtual environment, for example
`../../../metaquest-3d-reconstruction/.venv/Scripts/python.exe`.
