import argparse
import json
import math
import os
from pathlib import Path
import sys

import bpy
from mathutils import Vector


def parse_args():
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    else:
        argv = []

    parser = argparse.ArgumentParser(description="Render and validate animated GLB samples in Blender.")
    parser.add_argument("--case", nargs=3, action="append", metavar=("ID", "MODEL", "OUTDIR"), required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--resolution", default="960x720")
    parser.add_argument("--closeup", action="store_true", help="Frame each sampled pose as a readable close-up.")
    parser.add_argument("--motion-samples", type=int, default=24, help="Number of frames to inspect when choosing peak-motion frames.")
    return parser.parse_args(argv)


def reset_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for datablock_collection in (
        bpy.data.meshes,
        bpy.data.materials,
        bpy.data.images,
        bpy.data.armatures,
        bpy.data.actions,
        bpy.data.cameras,
        bpy.data.lights,
        bpy.data.collections,
    ):
        for datablock in list(datablock_collection):
            if datablock.users == 0:
                datablock_collection.remove(datablock)


def import_model(model_path):
    bpy.ops.import_scene.gltf(filepath=str(model_path))
    for obj in bpy.context.scene.objects:
        if obj.type == "ARMATURE":
            obj.hide_render = True
            obj.show_in_front = False
    bpy.context.view_layer.update()


def action_frame_range():
    ranges = []
    for action in bpy.data.actions:
        start, end = action.frame_range
        if math.isfinite(start) and math.isfinite(end) and end >= start:
            ranges.append((start, end))

    if not ranges:
        return 1, 1

    start = min(x[0] for x in ranges)
    end = max(x[1] for x in ranges)
    return int(math.floor(start)), int(math.ceil(end))


def sample_frames(start, end):
    if end <= start:
        return [start]
    mid = int(round((start + end) / 2))
    return sorted(set([start, mid, end]))


def candidate_motion_frames(start, end, limit):
    if end <= start:
        return [start]
    total = end - start + 1
    if total <= limit:
        return list(range(start, end + 1))
    step = (end - start) / max(limit - 1, 1)
    return sorted(set(int(round(start + i * step)) for i in range(limit)))


def pose_snapshot():
    result = {}
    for obj in bpy.context.scene.objects:
        if obj.type != "ARMATURE":
            continue
        for bone in obj.pose.bones:
            matrix = obj.matrix_world @ bone.matrix
            result[f"{obj.name}/{bone.name}"] = {
                "location": matrix.to_translation(),
                "rotation": matrix.to_quaternion(),
            }
    return result


def pose_motion_score(base, current):
    score = 0.0
    count = 0
    for name, value in current.items():
        if name not in base:
            continue
        base_value = base[name]
        score += (value["location"] - base_value["location"]).length
        dot = abs(value["rotation"].dot(base_value["rotation"]))
        dot = max(-1.0, min(1.0, dot))
        score += 0.25 * (2.0 * math.acos(dot))
        count += 1
    return score / count if count else 0.0


def select_motion_frames(start, end, motion_sample_count):
    frames = sample_frames(start, end)
    if end <= start:
        return frames, {"strategy": "singleFrame", "scores": []}

    scene = bpy.context.scene
    scene.frame_set(start)
    bpy.context.view_layer.update()
    base = pose_snapshot()
    scored = []
    for frame in candidate_motion_frames(start, end, motion_sample_count):
        if frame == start:
            continue
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        scored.append({"frame": frame, "score": pose_motion_score(base, pose_snapshot())})

    scored.sort(key=lambda x: x["score"], reverse=True)
    selected = [start]
    min_gap = max(2, int((end - start) * 0.15))
    for item in scored:
        frame = item["frame"]
        if all(abs(frame - existing) >= min_gap for existing in selected):
            selected.append(frame)
        if len(selected) == 3:
            break

    while len(selected) < 3:
        for fallback in [int(round((start + end) / 2)), end]:
            if fallback not in selected:
                selected.append(fallback)
            if len(selected) == 3:
                break

    return selected[:3], {
        "strategy": "startPlusTwoPeakMotionFrames",
        "scores": scored[:12],
    }


def evaluated_mesh_bounds():
    depsgraph = bpy.context.evaluated_depsgraph_get()
    points = []
    mesh_object_count = 0
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        mesh_object_count += 1
        evaluated = obj.evaluated_get(depsgraph)
        for corner in evaluated.bound_box:
            points.append(evaluated.matrix_world @ Vector(corner))

    if not points:
        return None

    min_v = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    max_v = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    size = max_v - min_v
    center = (min_v + max_v) * 0.5
    return {
        "meshObjectCount": mesh_object_count,
        "min": list(min_v),
        "max": list(max_v),
        "size": list(size),
        "center": list(center),
        "diagonal": float(size.length),
    }


def robust_evaluated_mesh_bounds(trim_ratio=0.01):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    points = []
    mesh_object_count = 0
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        mesh_object_count += 1
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        try:
            for vertex in mesh.vertices:
                points.append(evaluated.matrix_world @ vertex.co)
        finally:
            evaluated.to_mesh_clear()

    if not points:
        return None

    if len(points) < 64:
        trim_ratio = 0.0
    lo = int(len(points) * trim_ratio)
    hi = max(lo + 1, int(len(points) * (1.0 - trim_ratio))) - 1

    def trimmed(values):
        values = sorted(values)
        return values[lo], values[hi]

    min_x, max_x = trimmed([p.x for p in points])
    min_y, max_y = trimmed([p.y for p in points])
    min_z, max_z = trimmed([p.z for p in points])
    min_v = Vector((min_x, min_y, min_z))
    max_v = Vector((max_x, max_y, max_z))
    size = max_v - min_v
    center = (min_v + max_v) * 0.5
    return {
        "meshObjectCount": mesh_object_count,
        "trimRatio": trim_ratio,
        "min": list(min_v),
        "max": list(max_v),
        "size": list(size),
        "center": list(center),
        "diagonal": float(size.length),
    }


def union_bounds(bounds_list):
    valid = [b for b in bounds_list if b is not None]
    if not valid:
        return None
    min_v = Vector((min(b["min"][0] for b in valid), min(b["min"][1] for b in valid), min(b["min"][2] for b in valid)))
    max_v = Vector((max(b["max"][0] for b in valid), max(b["max"][1] for b in valid), max(b["max"][2] for b in valid)))
    size = max_v - min_v
    center = (min_v + max_v) * 0.5
    return {
        "min": list(min_v),
        "max": list(max_v),
        "size": list(size),
        "center": list(center),
        "diagonal": float(size.length),
    }


def setup_camera_and_lighting(bounds, resolution, closeup=False):
    width, height = resolution
    scene = bpy.context.scene
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.film_transparent = False
    scene.world = scene.world or bpy.data.worlds.new("World")
    scene.world.color = (0.78, 0.80, 0.82)

    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_WORKBENCH"

    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "Medium High Contrast"
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1

    center = Vector(bounds["center"])
    size = Vector(bounds["size"])
    max_size = max(size.x, size.y, size.z, 0.01)
    distance = max_size * 2.3

    bpy.ops.object.light_add(type="AREA", location=(center.x + distance * 0.4, center.y - distance * 0.8, center.z + distance * 0.8))
    light = bpy.context.object
    light.name = "Validation_Key_Light"
    light.data.energy = 600
    light.data.size = max_size * 2.0

    bpy.ops.object.camera_add(location=(center.x + distance * 0.75, center.y - distance * 1.25, center.z + distance * 0.55))
    camera = bpy.context.object
    frame_camera(camera, bounds, resolution, closeup=closeup)
    camera.data.clip_end = max(distance * 10, 1000)
    scene.camera = camera
    return camera


def frame_camera(camera, bounds, resolution, closeup=False):
    center = Vector(bounds["center"])
    size = Vector(bounds["size"])
    max_size = max(size.x, size.y, size.z, 0.01)
    distance = max(max_size * 2.3, 2.0)
    camera.location = (center.x + distance * 0.75, center.y - distance * 1.25, center.z + distance * 0.55)
    direction = center - camera.location
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    camera.data.clip_end = max(distance * 10, 1000)

    if closeup:
        width, height = resolution
        aspect = width / max(height, 1)
        vertical_scale = max(size.z / 0.68, max(size.x, size.y) / max(aspect * 0.76, 0.01), 0.65)
        camera.data.type = "ORTHO"
        camera.data.ortho_scale = vertical_scale
    else:
        camera.data.type = "PERSP"
        camera.data.lens = 45


def render_frame(frame, output_path, camera=None, resolution=(960, 720), closeup=False):
    scene = bpy.context.scene
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    bounds = evaluated_mesh_bounds()
    if closeup and camera and bounds:
        frame_camera(camera, robust_evaluated_mesh_bounds() or bounds, resolution, closeup=True)
        bpy.context.view_layer.update()
    scene.render.filepath = str(output_path)
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images["Render Result"]
    pixels = list(image.pixels)
    return pixels, bounds


def render_rest_pose(output_path, camera=None, resolution=(960, 720), closeup=False):
    scene = bpy.context.scene
    previous_pose_positions = []
    for obj in bpy.context.scene.objects:
        if obj.type == "ARMATURE":
            previous_pose_positions.append((obj, obj.data.pose_position))
            obj.data.pose_position = "REST"

    scene.frame_set(scene.frame_start)
    bpy.context.view_layer.update()
    bounds = evaluated_mesh_bounds()
    if closeup and camera and bounds:
        frame_camera(camera, robust_evaluated_mesh_bounds() or bounds, resolution, closeup=True)
        bpy.context.view_layer.update()
    scene.render.filepath = str(output_path)
    bpy.ops.render.render(write_still=True)

    for obj, pose_position in previous_pose_positions:
        obj.data.pose_position = pose_position
    bpy.context.view_layer.update()
    return bounds


def pixel_diff(a, b):
    if not a or not b or len(a) != len(b):
        return None
    count = len(a) // 4
    changed = 0
    sad = 0.0
    max_delta = 0.0
    for i in range(0, len(a), 4):
        d = abs(a[i] - b[i]) + abs(a[i + 1] - b[i + 1]) + abs(a[i + 2] - b[i + 2])
        sad += d
        max_delta = max(max_delta, d)
        if d > 0.08:
            changed += 1
    return {
        "changedPixels": changed,
        "changedRatio": changed / count if count else 0,
        "meanAbsRgbDelta": sad / (count * 3) if count else 0,
        "maxRgbTripletDelta": max_delta,
    }


def count_vertices():
    return sum(len(obj.data.vertices) for obj in bpy.context.scene.objects if obj.type == "MESH")


def vertex_outlier_stats():
    depsgraph = bpy.context.evaluated_depsgraph_get()
    points = []
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        try:
            for vertex in mesh.vertices:
                points.append(evaluated.matrix_world @ vertex.co)
        finally:
            evaluated.to_mesh_clear()

    if len(points) < 16:
        return None

    center = Vector((
        sum(p.x for p in points) / len(points),
        sum(p.y for p in points) / len(points),
        sum(p.z for p in points) / len(points),
    ))
    distances = sorted((p - center).length for p in points)
    p50 = distances[int(len(distances) * 0.50)]
    p95 = distances[int(len(distances) * 0.95)]
    max_distance = distances[-1]
    threshold = max(p95 * 2.5, p50 * 4.0, 0.1)
    outliers = sum(1 for value in distances if value > threshold)
    return {
        "vertexCount": len(points),
        "distanceP50": p50,
        "distanceP95": p95,
        "maxDistance": max_distance,
        "outlierThreshold": threshold,
        "outlierCount": outliers,
        "outlierRatio": outliers / len(points),
        "maxToP95Ratio": max_distance / p95 if p95 > 1e-6 else 0,
    }


def pose_bone_length_stats():
    ratios = []
    scales = []
    worst_lengths = []
    worst_scales = []
    for obj in bpy.context.scene.objects:
        if obj.type != "ARMATURE":
            continue
        for bone in obj.pose.bones:
            rest = obj.data.bones.get(bone.name)
            if rest is None:
                continue
            rest_length = max(rest.length, 1e-6)
            pose_length = max((bone.tail - bone.head).length, 1e-6)
            ratio = pose_length / rest_length
            ratios.append(ratio)
            if ratio > 3.0 or ratio < 0.33:
                worst_lengths.append({"bone": bone.name, "ratio": ratio, "restLength": rest_length, "poseLength": pose_length})
            max_scale = max(abs(bone.scale.x), abs(bone.scale.y), abs(bone.scale.z))
            min_scale = min(abs(bone.scale.x), abs(bone.scale.y), abs(bone.scale.z))
            scales.append(max_scale)
            if max_scale > 3.0 or min_scale < 0.1:
                worst_scales.append({"bone": bone.name, "scale": [bone.scale.x, bone.scale.y, bone.scale.z]})

    return {
        "boneCount": len(ratios),
        "maxBoneLengthRatio": max(ratios) if ratios else 0,
        "minBoneLengthRatio": min(ratios) if ratios else 0,
        "maxPoseScale": max(scales) if scales else 0,
        "lengthWarnings": worst_lengths[:16],
        "scaleWarnings": worst_scales[:16],
    }


def frame_structural_checks(first_bounds, bounds, vertex_stats, bone_stats):
    warnings = []
    if first_bounds and bounds:
        first_diag = max(first_bounds.get("diagonal", 0), 1e-6)
        diag = bounds.get("diagonal", 0)
        if diag / first_diag > 1.8:
            warnings.append(f"bbox diagonal grew {diag / first_diag:.2f}x from first frame")
        first_center = Vector(first_bounds["center"])
        center = Vector(bounds["center"])
        if (center - first_center).length > first_diag * 1.2:
            warnings.append("bbox center moved unusually far from first frame")
    if vertex_stats:
        if vertex_stats["outlierRatio"] > 0.01 or vertex_stats["maxToP95Ratio"] > 3.5:
            warnings.append("vertex outlier distribution is suspicious")
    if bone_stats:
        if bone_stats["lengthWarnings"]:
            warnings.append("pose bone length ratio is suspicious")
        if bone_stats["scaleWarnings"]:
            warnings.append("pose bone scale is suspicious")
    return warnings


def validate_case(case_id, model_path, output_dir, resolution, closeup=False, motion_sample_count=24):
    reset_scene()
    output_dir.mkdir(parents=True, exist_ok=True)
    import_model(model_path)

    mesh_objects = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    materials = {slot.material.name for obj in mesh_objects for slot in obj.material_slots if slot.material}
    images = {image.filepath or image.name for image in bpy.data.images if image.name != "Render Result"}
    actions = list(bpy.data.actions)
    start, end = action_frame_range()
    frames, motion_selection = select_motion_frames(start, end, motion_sample_count)

    frame_bounds = []
    for frame in frames:
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        frame_bounds.append(evaluated_mesh_bounds())
    bounds = union_bounds(frame_bounds) or {
        "min": [-1, -1, -1],
        "max": [1, 1, 1],
        "size": [2, 2, 2],
        "center": [0, 0, 0],
        "diagonal": 3.464,
    }
    camera = setup_camera_and_lighting(bounds, resolution, closeup=closeup)

    rendered = []
    rest_pose_path = output_dir / f"{case_id}_rest_pose.png"
    rest_bounds = render_rest_pose(rest_pose_path, camera=camera, resolution=resolution, closeup=closeup)
    base_pixels = None
    first_bounds = None
    for frame in frames:
        output_path = output_dir / f"{case_id}_frame_{frame:04d}.png"
        pixels, bounds_at_frame = render_frame(frame, output_path, camera=camera, resolution=resolution, closeup=closeup)
        if first_bounds is None:
            first_bounds = bounds_at_frame
        vertex_stats = vertex_outlier_stats()
        bone_stats = pose_bone_length_stats()
        diff = None if base_pixels is None else pixel_diff(base_pixels, pixels)
        if base_pixels is None:
            base_pixels = pixels
        rendered.append({
            "frame": frame,
            "path": str(output_path),
            "bounds": bounds_at_frame,
            "vertexOutliers": vertex_stats,
            "boneStats": bone_stats,
            "structuralWarnings": frame_structural_checks(first_bounds, bounds_at_frame, vertex_stats, bone_stats),
            "diffFromFirst": diff,
        })

    result = {
        "caseId": case_id,
        "model": str(model_path),
        "status": "ok" if mesh_objects and armatures and actions else "warning",
        "meshObjectCount": len(mesh_objects),
        "vertexCount": count_vertices(),
        "armatureCount": len(armatures),
        "boneCount": sum(len(arm.data.bones) for arm in armatures),
        "materialCount": len(materials),
        "imageCount": len(images),
        "actionCount": len(actions),
        "frameRange": [start, end],
        "sampledFrames": frames,
        "motionSelection": motion_selection,
        "closeup": closeup,
        "unionBounds": bounds,
        "restPoseBounds": rest_bounds,
        "restPoseFrame": str(rest_pose_path),
        "renderedFrames": rendered,
        "hasVisibleFrameChange": any((r["diffFromFirst"] or {}).get("changedRatio", 0) > 0.005 for r in rendered),
        "hasGeometryBoundsChange": max((r["bounds"] or {}).get("diagonal", 0) for r in rendered) - min((r["bounds"] or {}).get("diagonal", 0) for r in rendered) > 0.001,
        "visualAcceptance": {
            "status": "requiresManualReview",
            "reason": "Automated mesh/action/frame-diff checks only prove the preview pipeline ran. Humanoid animation correctness requires manual visual review for deformation, twisted limbs, flying attachments, bad bind pose, scale issues, and semantically plausible pose.",
            "minimumEvidence": [
                "rest pose screenshot",
                "animation start frame",
                "animation middle frame",
                "animation end frame"
            ]
        },
    }
    report_path = output_dir / f"{case_id}_blender_visual_validation.json"
    report_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    result["report"] = str(report_path)
    return result


def main():
    args = parse_args()
    width, height = [int(x) for x in args.resolution.lower().split("x", 1)]
    resolution = (width, height)
    summary = []
    for case_id, model, outdir in args.case:
        try:
            summary.append(validate_case(case_id, Path(model), Path(outdir), resolution, closeup=args.closeup, motion_sample_count=args.motion_samples))
        except Exception as exc:
            output_dir = Path(outdir)
            output_dir.mkdir(parents=True, exist_ok=True)
            result = {
                "caseId": case_id,
                "model": str(model),
                "status": "error",
                "error": str(exc),
                "visualAcceptance": {
                    "status": "failed",
                    "reason": "Blender could not import and render this preview. A reusable humanoid animation sample must be openable before visual acceptance."
                }
            }
            report_path = output_dir / f"{case_id}_blender_visual_validation.json"
            report_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
            result["report"] = str(report_path)
            summary.append(result)
    summary_path = Path(args.summary)
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
