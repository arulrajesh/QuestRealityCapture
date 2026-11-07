"""Wire-format cross-check between the C# headset client and the Python laptop receiver."""
import io
import sys
from pathlib import Path

import numpy as np

from mq3drecon.models import Side
from mq3drecon.streaming.feedback import KeyframeNotice, LiveStatusUpdate, pack_surface_mesh
from mq3drecon.streaming.framing import read_frame
from mq3drecon.streaming.messages import (
    ColorFrame, DepthFrame, Heartbeat, Hello, Marker, Pose, RoomModelUpdate, SessionEnd, SessionStart, decode_message, encode_message,
)
from mq3drecon.streaming.payloads import decode_depth_ndc

mode, path = sys.argv[1], Path(sys.argv[2])
if mode == "decode-csharp":
    stream = io.BytesIO(path.read_bytes())
    messages = []
    while (raw := read_frame(stream)) is not None:
        messages.append(decode_message(raw))
    kinds = [type(m).__name__ for m in messages]
    print("decoded from C#:", kinds)
    hello, start, color, depth_u16, depth_f32, heartbeat, end = messages[:7]
    assert isinstance(hello, Hello) and hello.device == 'Quest 3 "stub"'
    assert isinstance(start, SessionStart) and start.session_name == "20261106_130000" and not start.resume
    assert start.intrinsics[Side.LEFT]["principalPoint"]["x"] == 642.97 and Side.RIGHT in start.intrinsics
    assert isinstance(color, ColorFrame) and color.side == Side.LEFT and color.timestamp_us == 1793946809031963
    assert color.encoding == "jpeg" and color.row_order == "top_down" and color.data[:2] == b"\xff\xd8"
    assert abs(color.pose.rotation[3] - 0.9997) < 1e-6
    assert isinstance(depth_u16, DepthFrame) and depth_u16.side == Side.RIGHT and np.isinf(depth_u16.far_z)
    ndc = decode_depth_ndc(depth_u16)
    assert ndc.shape == (2, 3) and np.allclose(ndc.ravel(), [0, 0.5, 0.98, 1, 0, 0.25], atol=1 / 65535)
    ndc32 = decode_depth_ndc(depth_f32)
    assert np.isnan(ndc32.ravel()[4]) and abs(ndc32.ravel()[2] - 0.98) < 1e-7
    assert isinstance(heartbeat, Heartbeat) and heartbeat.dropped == {"all": 1}
    assert isinstance(end, SessionEnd) and end.reason == "user_stop"
    room, marker = messages[8:10]
    assert isinstance(room, RoomModelUpdate) and room.room["entrance"]["anchor_uuid"] == "d1"
    assert isinstance(marker, Marker) and marker.kind == "detail" and marker.data == {"start_us": 1793946809031963, "end_us": 1793946811031963}
    print("C# -> Python: OK")
elif mode == "encode-feedback":
    pose = Pose((0.1, 1.6, -0.2), (0.0, 0.38268343, 0.0, 0.92387953))
    vertices = np.array([[0.0, 0.0, 0.0], [1.0, 0.0, -1.0], [0.0, 1.0, -0.5]])
    mesh = pack_surface_mesh(vertices, np.array([[0, 1, 2]]), np.full((3, 3), 0.5), np.array([1.0, 0.5, 0.0]), sequence=7)
    blob = b"".join(encode_message(m) for m in [
        KeyframeNotice(1793946809031963, True, True, "first", 0.99, 51234.5, pose),
        mesh,
        LiveStatusUpdate({"session": "20261106_130000", "recording": True, "keyframes": 12, "surface_m2": 3.25, "triangles": 1}),
    ])
    path.write_bytes(blob)
    print(f"Python feedback encoded ({len(blob)} bytes); first unity x = {mesh.unpack()[0][0][0]:.3f}")
