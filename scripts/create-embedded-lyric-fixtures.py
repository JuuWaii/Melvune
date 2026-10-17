"""Create valid audio/tag samples with FFmpeg, without reading or changing user audio."""
import argparse
import hashlib
import json
import pathlib
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("output", type=pathlib.Path)
parser.add_argument("--ffmpeg", type=pathlib.Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
timed = "[00:00]内嵌第一句\n[00:03]内嵌第二句\n[00:06]内嵌第三句"
plain = "内嵌纯文本\n第二句"
specs = [("mp3", "libmp3lame", "lyrics-eng"), ("flac", "flac", "lyrics"),
         ("m4a", "aac", "lyrics"), ("ogg", "libvorbis", "lyrics"),
         ("opus", "libopus", "lyrics"), ("wma", "wmav2", "WM/Lyrics")]
records = []
for extension, codec, key in specs:
    for kind, text in [("timed", timed), ("plain", plain)]:
        name = f"{kind}.{extension}"
        output = args.output / name
        metadata = args.output / "input.ffmetadata"
        escaped = text.replace("\\", "\\\\").replace("=", "\\=").replace(";", "\\;").replace("#", "\\#").replace("\n", "\\\n")
        metadata.write_text(f";FFMETADATA1\ntitle={kind}-{extension}\nartist=Melvune fixture\n{key}={escaped}\n", encoding="utf-8", newline="\n")
        command = [str(args.ffmpeg), "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                   "-f", "lavfi", "-i", "sine=frequency=440:duration=30", "-i", str(metadata),
                   "-map", "0:a", "-map_metadata", "1", "-c:a", codec]
        if extension == "mp3":
            command += ["-id3v2_version", "3"]
        command += [str(output)]
        subprocess.run(command, check=True, timeout=30, capture_output=True)
        records.append({"file": name, "kind": kind, "bytes": output.stat().st_size,
                        "sha256": hashlib.sha256(output.read_bytes()).hexdigest()})
metadata.unlink()
(args.output / "fixtures.json").write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"created": len(records), "formats": [s[0] for s in specs]}))
