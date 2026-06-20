import argparse
import json
import math
from collections import Counter, defaultdict
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

    parser = argparse.ArgumentParser(description="Diagnose animated GLB pose deformation in Blender.")
    parser.add_argument("--model", required=True)
    parser.add_argument("--frame", type=int, required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--top", type=int, default=64)
    return parser.parse_args(argv)


def reset_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for collection in (
        bpy.data.meshes,
        bpy.data.materials,
        bpy.data.images,
        bpy.data.armatures,
        bpy.data.actions,
        bpy.data.cameras,
        bpy.data.lights,
        bpy.data.collections,
    ):
        for datablock in list(collection):
            if datablock.users == 0:
                collection.remove(datablock)


def import_model(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    bpy.context.view_layer.update()


def set_rest_pose():
    for obj in bpy.context.scene.objects:
        if obj.type == "ARMATURE":
            obj.data.pose_position = "REST"
    bpy.context.view_layer.update()


def set_pose_frame(frame):
    for obj in bpy.context.scene.objects:
        if obj.type == "ARMATURE":
            obj.data.pose_position = "POSE"
    bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()


def evaluated_vertices(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        return [evaluated.matrix_world @ vertex.co for vertex in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


def vertex_materials(obj):
    names = {}
    for polygon in obj.data.polygons:
        mat = obj.material_slots[polygon.material_index].material if polygon.material_index < len(obj.material_slots) else None
        name = mat.name if mat else f"slot_{polygon.material_index}"
        for vertex_index in polygon.vertices:
            names.setdefault(vertex_index, name)
    return names


def vertex_weights(obj, vertex_index, limit=8):
    vertex = obj.data.vertices[vertex_index]
    weights = []
    for group in vertex.groups:
        if group.group >= len(obj.vertex_groups):
            continue
        weights.append({"group": obj.vertex_groups[group.group].name, "weight": float(group.weight)})
    weights.sort(key=lambda x: x["weight"], reverse=True)
    return weights[:limit]


def armature_parent(obj):
    if obj.parent and obj.parent.type == "ARMATURE":
        return obj.parent
    for modifier in obj.modifiers:
        if modifier.type == "ARMATURE" and modifier.object:
            return modifier.object
    return None


def bone_world_heads(armature, pose=False):
    result = {}
    if pose:
        for bone in armature.pose.bones:
            result[bone.name] = {
                "head": armature.matrix_world @ bone.head,
                "tail": armature.matrix_world @ bone.tail,
                "matrix": armature.matrix_world @ bone.matrix,
            }
    else:
        for bone in armature.data.bones:
            result[bone.name] = {
                "head": armature.matrix_world @ bone.head_local,
                "tail": armature.matrix_world @ bone.tail_local,
                "matrix": armature.matrix_world @ bone.matrix_local,
            }
    return result


def quat_angle(a, b):
    dot = abs(a.dot(b))
    dot = max(-1.0, min(1.0, dot))
    return 2.0 * math.acos(dot)


def diagnose_bones(rest_bones, pose_bones, limit):
    rows = []
    for name, pose in pose_bones.items():
        rest = rest_bones.get(name)
        if not rest:
            continue
        rest_head = rest["head"]
        pose_head = pose["head"]
        rest_tail = rest["tail"]
        pose_tail = pose["tail"]
        rest_len = max((rest_tail - rest_head).length, 1e-6)
        pose_len = max((pose_tail - pose_head).length, 1e-6)
        rows.append(
            {
                "bone": name,
                "headDisplacement": float((pose_head - rest_head).length),
                "tailDisplacement": float((pose_tail - rest_tail).length),
                "lengthRatio": float(pose_len / rest_len),
                "rotationAngleRadians": float(quat_angle(rest["matrix"].to_quaternion(), pose["matrix"].to_quaternion())),
                "restHead": list(rest_head),
                "poseHead": list(pose_head),
            }
        )
    rows.sort(key=lambda x: (x["headDisplacement"], x["rotationAngleRadians"]), reverse=True)
    return rows[:limit]


def summarize_displacements(displacements):
    if not displacements:
        return {}
    values = sorted(displacements)
    return {
        "count": len(values),
        "p50": values[int(len(values) * 0.50)],
        "p90": values[int(len(values) * 0.90)],
        "p95": values[int(len(values) * 0.95)],
        "p99": values[int(len(values) * 0.99)],
        "max": values[-1],
    }


def diagnose_mesh(obj, rest_vertices, pose_vertices, top):
    materials = vertex_materials(obj)
    rows = []
    material_counter = Counter()
    group_weight_sum = defaultdict(float)
    group_hits = Counter()
    displacements = []
    count = min(len(rest_vertices), len(pose_vertices), len(obj.data.vertices))
    for index in range(count):
        rest = rest_vertices[index]
        pose = pose_vertices[index]
        displacement = float((pose - rest).length)
        displacements.append(displacement)
        material = materials.get(index, "")
        material_counter[material] += displacement
        for weight in vertex_weights(obj, index, limit=16):
            group_weight_sum[weight["group"]] += displacement * weight["weight"]
            group_hits[weight["group"]] += 1
        rows.append(
            {
                "vertexIndex": index,
                "displacement": displacement,
                "material": material,
                "rest": list(rest),
                "pose": list(pose),
                "weights": vertex_weights(obj, index),
            }
        )

    rows.sort(key=lambda x: x["displacement"], reverse=True)
    top_groups = sorted(group_weight_sum.items(), key=lambda x: x[1], reverse=True)[:top]
    return {
        "object": obj.name,
        "armature": armature_parent(obj).name if armature_parent(obj) else None,
        "vertexCount": count,
        "displacementStats": summarize_displacements(displacements),
        "topVertices": rows[:top],
        "topMaterialsByWeightedDisplacement": [
            {"material": key, "sumDisplacement": value} for key, value in material_counter.most_common(top)
        ],
        "topVertexGroupsByWeightedDisplacement": [
            {"group": key, "weightedDisplacement": value, "hitCount": group_hits[key]} for key, value in top_groups
        ],
    }


def main():
    args = parse_args()
    reset_scene()
    model = Path(args.model)
    import_model(model)

    mesh_objects = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]

    set_rest_pose()
    rest_vertices = {obj.name: evaluated_vertices(obj) for obj in mesh_objects}
    rest_bones = {}
    for armature in armatures:
        rest_bones[armature.name] = bone_world_heads(armature, pose=False)

    set_pose_frame(args.frame)
    pose_vertices = {obj.name: evaluated_vertices(obj) for obj in mesh_objects}
    pose_bones = {}
    for armature in armatures:
        pose_bones[armature.name] = bone_world_heads(armature, pose=True)

    result = {
        "model": str(model),
        "frame": args.frame,
        "meshCount": len(mesh_objects),
        "armatureCount": len(armatures),
        "meshes": [
            diagnose_mesh(obj, rest_vertices[obj.name], pose_vertices[obj.name], args.top)
            for obj in mesh_objects
        ],
        "armatures": [
            {
                "armature": armature.name,
                "boneCount": len(armature.data.bones),
                "topBonesByPoseChange": diagnose_bones(rest_bones[armature.name], pose_bones[armature.name], args.top),
            }
            for armature in armatures
        ],
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "output": str(output),
        "meshCount": result["meshCount"],
        "armatureCount": result["armatureCount"],
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
