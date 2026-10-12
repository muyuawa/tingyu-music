"""Convert captured player frames into a loop and compare the same background pose.

This processes diagnostic files only. It is not part of the glass renderer.
"""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument("directory", type=Path)
args = parser.parse_args()
folder = args.directory.resolve()
report = json.loads((folder / "UI_RENDER_VERIFICATION.json").read_text(encoding="utf-8-sig"))
frames = [Image.open(folder / f"refraction-{i:02}.png").convert("RGB") for i in range(report["frames"])]
baseline = np.asarray(Image.open(folder / "refraction-baseline.png").convert("RGB"), dtype=np.float32)
actual = np.asarray(frames[0], dtype=np.float32)
delta = np.abs(actual - baseline).mean(axis=2)
scale = report["dpi_scale"]
bounds = report["transport"]
# Window chrome can add a few pixels; keep the comparison band inside the bounds.
x, y, w, h = [int(bounds[key] * scale) for key in ("x", "y", "width", "height")]
rim = delta[y + 7:y + 20, x + 100:x + w - 100]
outside = delta[250:950, 120:700]
movement = [float(np.abs(np.asarray(a, dtype=np.float32) - np.asarray(b, dtype=np.float32)).mean()) for a, b in zip(frames, frames[1:])]
small = [im.resize((1440, round(im.height * 1440 / im.width)), Image.Resampling.LANCZOS) for im in frames]
sequence = small + small[-2:0:-1]
sequence[0].save(folder / "dynamic-refraction.gif", save_all=True, append_images=sequence[1:], duration=180, loop=0, optimize=False)
detail = [im.crop((max(0, x - 4), max(0, y - 30), min(im.width, x + w + 20), min(im.height, y + h + 20))) for im in frames]
detail[0].save(folder / "glass-refraction-detail.gif", save_all=True, append_images=detail[1:] + detail[-2:0:-1], duration=180, loop=0, optimize=False)
no_blur = np.asarray(Image.open(folder / "refraction-without-blur.png").convert("RGB"), dtype=np.float32)
blur_difference = np.abs(actual - no_blur)[y + 40:y + h - 40, x + 40:x + w - 40].mean()
metrics = {
    "blur_comparison": "same frozen background position and same refraction; normal blur versus zero blur",
    "blur_mean_absolute_rgb_difference_0_to_255": round(float(blur_difference), 4),
    "comparison": "same background position; refraction=1, dispersion=0 versus normal material",
    "rim_mean_absolute_rgb_difference_0_to_255": round(float(rim.mean()), 4),
    "rim_fraction_pixels_difference_gt_3": round(float((rim > 3).mean()), 4),
    "outside_glass_mean_absolute_rgb_difference_0_to_255": round(float(outside.mean()), 4),
    "successive_full_frame_mean_absolute_rgb_difference": [round(v, 4) for v in movement],
    "motion_frames_distinct": all(v > 0 for v in movement),
    "interpretation": "Image differences and moving captures support visual inspection; they are not a frame-rate benchmark or a physical-optics validation.",
    "zero_blur_frame": "refraction-without-blur.png",
    "loop": "Original captured frames with reverse playback appended for a seamless inspection loop. No interpolated frames.",
}
if report.get("flow_frames"):
    flow = [Image.open(folder / f"flow-{i:02}.png").convert("RGB") for i in range(report["flow_frames"])]
    background_motion = [float(np.abs(np.asarray(a, dtype=np.float32)[120:220, 120:900] - np.asarray(b, dtype=np.float32)[120:220, 120:900]).mean()) for a, b in zip(flow, flow[1:])]
    resized = [im.resize((1200, round(im.height * 1200 / im.width)), Image.Resampling.LANCZOS) for im in flow]
    resized[0].save(folder / "flowing-background.gif", save_all=True, append_images=resized[1:], duration=report["flow_interval_ms"], loop=0, optimize=False)
    metrics["ambient_background_successive_rgb_difference"] = [round(v, 4) for v in background_motion]
    metrics["ambient_background_frames_distinct"] = all(v > 0 for v in background_motion)
    metrics["ambient_loop"] = "Actual natural-speed compositor animation frames; no interpolation, repeated loop has a boundary."
(folder / "DYNAMIC_EVIDENCE.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
overlap_report = folder / "TRANSPORT_OVERLAP_VERIFICATION.json"
if overlap_report.exists():
    overlap = json.loads(overlap_report.read_text(encoding="utf-8-sig"))
    scroll_frames = [Image.open(folder / f"library-scroll-{i:02}.png").convert("RGB") for i in range(overlap["scroll_frames"])]
    scroll_frames[0].save(folder / "library-under-glass.gif", save_all=True, append_images=scroll_frames[1:] + scroll_frames[-2:0:-1], duration=overlap["scroll_interval_ms"], loop=0, optimize=False)
    metrics["real_list_scroll_under_glass"] = {
        "layout_checks_pass": overlap["pass"],
        "frames": len(scroll_frames),
        "successive_rgb_difference": [round(float(np.abs(np.asarray(a, dtype=np.float32) - np.asarray(b, dtype=np.float32)).mean()), 4) for a, b in zip(scroll_frames, scroll_frames[1:])],
        "content": "actual ListView item containers; isolated example tracks; no interpolated frames",
        "loop": "original captured frames followed by reverse playback for inspection",
    }
    (folder / "DYNAMIC_EVIDENCE.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
polish_report = folder / "VISUAL_POLISH_VERIFICATION.json"
if polish_report.exists():
    polish = json.loads(polish_report.read_text(encoding="utf-8-sig"))
    vivid = [Image.open(folder / f"vivid-flow-{i:02}.png").convert("RGB") for i in range(polish["flow_frames"])]
    differences = [float(np.abs(np.asarray(a, dtype=np.float32)[90:220, 120:1800] - np.asarray(b, dtype=np.float32)[90:220, 120:1800]).mean()) for a, b in zip(vivid, vivid[1:])]
    resized = [im.resize((1200, round(im.height * 1200 / im.width)), Image.Resampling.LANCZOS) for im in vivid]
    resized[0].save(folder / "vivid-flowing-background.gif", save_all=True, append_images=resized[1:], duration=polish["flow_interval_ms"], loop=0, optimize=False)
    metrics["vivid_background"] = {"successive_background_rgb_difference": [round(v, 4) for v in differences], "frames_distinct": all(v > 0 for v in differences), "loop": "actual natural-speed compositor frames; no interpolation; loop has a boundary"}
queue_report = folder / "QUEUE_WAVE_VERIFICATION.json"
if queue_report.exists():
    queue = json.loads(queue_report.read_text(encoding="utf-8-sig"))
    bounds, scale = queue["marker_bounds"], queue["dpi_scale"]
    # Keep a small margin for the window's non-client border.
    x, y, w, h = [round(bounds[key] * scale) for key in ("X", "Y", "Width", "Height")]
    wave = [Image.open(folder / f"queue-wave-{i:02}.png").convert("RGB").crop((x - 4, y - 4, x + w + 16, y + h + 16)) for i in range(queue["frames"])]
    movement = [float(np.abs(np.asarray(a, dtype=np.float32) - np.asarray(b, dtype=np.float32)).mean()) for a, b in zip(wave, wave[1:])]
    wave[0].save(folder / "queue-playing-indicator.gif", save_all=True, append_images=wave[1:], duration=queue["interval_ms"], loop=0, optimize=False)
    queue.update({"successive_marker_rgb_difference": [round(v, 4) for v in movement], "frames_distinct": all(v > 0 for v in movement)})
    metrics["queue_playing_indicator"] = queue
    queue_report.write_text(json.dumps(queue, indent=2), encoding="utf-8")
(folder / "DYNAMIC_EVIDENCE.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
print(json.dumps(metrics, indent=2))
