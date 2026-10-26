"""Generate a tiny DOS COM to exercise multiple mounts and keyboard input."""
from pathlib import Path
import json
import argparse

parser = argparse.ArgumentParser()
parser.add_argument("--drive-d", type=Path)
parser.add_argument("--probe", default="D:\\MARKER.TXT")
parser.add_argument("--profile-name", default="Multiple drives validation")
parser.add_argument("--output-name", default="multi-drive.json")
args = parser.parse_args()

root = Path(__file__).resolve().parents[1]
folder = root / "tests" / "fixtures"
drive_c, drive_d = folder / "drive-c", folder / "drive-d"
if args.drive_d:
    drive_d = args.drive_d.resolve()
drive_c.mkdir(parents=True, exist_ok=True)
drive_d.mkdir(parents=True, exist_ok=True)
if not args.drive_d:
    (drive_d / "MARKER.TXT").write_text("Mounted data drive", encoding="ascii")
code = bytearray()
labels = {}
fixups = []

def emit(*values):
    code.extend(values)

def address(label):
    emit(0xBA)  # mov dx, imm16
    fixups.append((len(code), label, "address"))
    emit(0, 0)

def branch(opcode, label):
    emit(opcode)
    fixups.append((len(code), label, "branch"))
    emit(0)

address("filename")
emit(0xB8, 0x00, 0x3D, 0xCD, 0x21)  # open D:\MARKER.TXT read-only
branch(0x72, "failure")  # jc
emit(0x89, 0xC3, 0xB4, 0x3E, 0xCD, 0x21)  # close returned handle
address("success")
branch(0xEB, "print")
labels["failure"] = len(code)
address("error")
labels["print"] = len(code)
emit(0xB4, 0x09, 0xCD, 0x21)
labels["keyloop"] = len(code)
emit(0x31, 0xC0, 0xCD, 0x16)  # wait for a BIOS keyboard key
address("keymsg")
emit(0xB4, 0x09, 0xCD, 0x21)
branch(0xEB, "keyloop")
for label, value in {
    "filename": args.probe.encode("ascii") + b"\0",
    "success": b"\r\nMULTIPLE DRIVES VERIFIED - D: marker opened\r\n$",
    "error": b"\r\nERROR: D: marker could not be opened\r\n$",
    "keymsg": b"KEYBOARD VERIFIED\r\n$",
}.items():
    labels[label] = len(code)
    code.extend(value)
for offset, label, kind in fixups:
    if kind == "address":
        code[offset:offset + 2] = (0x100 + labels[label]).to_bytes(2, "little")
    else:
        code[offset] = (labels[label] - offset - 1) & 0xff
(drive_c / "VERIFY.COM").write_bytes(code)
(folder / args.output_name).write_text(json.dumps({
    "Id": "validation-multi-drive", "Name": args.profile_name,
    "Drives": [{"Letter": "C", "Folder": str(drive_c)}, {"Letter": "D", "Folder": str(drive_d)}],
    "Startup": "C:\\VERIFY.COM", "Cycles": "auto", "Memory": 32,
}, indent=2), encoding="utf-8")
