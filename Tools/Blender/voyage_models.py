"""Builds the Bottle Voyage props in Blender, bakes texture atlases and exports FBX meshes for Unity.

Run headless from the repository root:
    blender -b --factory-startup --python Tools/Blender/voyage_models.py -- [rope] [fittings] [boat] [desk] [save]
With no group names everything is rebuilt. Unity's "Water Splash/Polish Bottle Voyage" assigns the results.
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy
import vcore as vc
import vfittings

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ALL = ["rope", "fittings", "boat", "desk"]
groups = [a for a in ARGS if a in ALL] or ALL

vc.reset()
if "rope" in groups:
    vfittings.rope_tile()
if "fittings" in groups:
    parts, ropes = vfittings.bottle_parts()
    vc.export(parts + ropes, "Bottle fittings.fbx")
if "boat" in groups:
    import vboat
    vc.export(vboat.build(), "Sailboat.fbx")
if "desk" in groups:
    import vdesk
    vc.export(vdesk.build(), "Desk props.fbx")
if "save" in ARGS:
    os.makedirs(os.path.join(vc.HERE, "build"), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(vc.HERE, "build", "voyage_models.blend"))
print("VOYAGE BUILD DONE", groups)
