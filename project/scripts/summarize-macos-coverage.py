"""Aggregate actual production source lines, retaining unmeasured projects as gate failures."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--project", required=True)
parser.add_argument("--results", required=True)
parser.add_argument("--threshold", type=float, default=90)
args = parser.parse_args()
root = Path(args.project).resolve()
results = Path(args.results).resolve()
projects = {}
for item in ET.parse(root / "AiUsageMonitor.Mac.slnx").iter("Project"):
    path = item.get("Path", "")
    if path.startswith("src/"):
        project = root / path
        name = ET.parse(project).findtext(".//AssemblyName") or project.stem
        projects[name] = project.parent
lines = {}
seen = set()
for report in results.rglob("coverage.cobertura.xml"):
    for package in ET.parse(report).findall(".//package"):
        name = package.get("name")
        if name not in projects:
            continue
        for cls in package.findall("./classes/class"):
            filename = cls.get("filename", "").replace("\\", "/")
            if "/obj/" in filename or "/bin/" in filename:
                continue
            marker = "/src/" + projects[name].name + "/"
            if marker in "/" + filename:
                filename = ("/" + filename).split(marker, 1)[1]
            source = Path(filename)
            if not source.is_absolute():
                if filename.startswith(projects[name].name + "/"):
                    source = projects[name].parent / filename
                else:
                    source = projects[name] / filename
            source = source.resolve()
            if not source.is_file() or projects[name] not in source.parents:
                continue
            seen.add(name)
            for line in cls.findall("./lines/line"):
                key = (str(source.relative_to(root)), int(line.get("number")))
                lines[key] = lines.get(key, False) or int(line.get("hits")) > 0
native = json.loads((results / "native-coverage.json").read_text())
native_lines = native["data"][0]["totals"]["lines"]
covered = sum(lines.values()) + native_lines["covered"]
valid = len(lines) + native_lines["count"]
percent = covered / valid * 100 if valid else 0
missing = sorted(set(projects) - seen)
manifest = {
    "schema_version": "1.0", "os": "macos", "covered": covered, "valid": valid,
    "coverage_percent": round(percent, 2), "unmeasured_projects": missing,
    "native": {"sources": ["src/AiUsageMonitor.Platform.Mac/Native/process-supervisor.c", "src/AiUsageMonitor.Platform.Mac/Native/local-socket.c"], "lines": native_lines},
    "projects": [{"name": name, "measured": name in seen,
                  "covered": sum(hit for (file, _), hit in lines.items() if file.startswith("src/" + directory.name + "/")),
                  "valid": sum(1 for file, _ in lines if file.startswith("src/" + directory.name + "/"))}
                 for name, directory in projects.items()],
    "source_sha256": {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
                      for directory in projects.values() for path in directory.rglob("*")
                      if path.is_file() and path.suffix in (".cs", ".c", ".axaml", ".csproj")
                      and "obj" not in path.parts and "bin" not in path.parts},
    "status": "PASS" if not missing and percent >= args.threshold else "FAIL",
}
(results / "coverage-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
print(f"Mac production coverage: {percent:.2f}% ({covered}/{valid}), unmeasured={len(missing)}, gate={manifest['status']}")
raise SystemExit(0 if manifest["status"] == "PASS" else 1)
