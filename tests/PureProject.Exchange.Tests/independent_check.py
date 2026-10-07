"""Independent read-only checks with openpyxl and Python XML/ZIP/JSON parsers."""
import base64, hashlib, json, pathlib, sys, zipfile, xml.etree.ElementTree as ET
import openpyxl

root = pathlib.Path(sys.argv[1])
wb = openpyxl.load_workbook(root / "sample.xlsx", data_only=False, read_only=False)
sheet = wb.worksheets[0]
assert sheet["C2"].value == "中文项目 =SUM(A1:A2)"
assert sheet["C12"].data_type == "s" and sheet["C12"].value.startswith("=HYPERLINK")
assert sheet["G12"].value.strftime("%Y-%m-%d") == "2026-11-01"
assert sheet["H12"].value.strftime("%H:%M") == "09:30"
assert sheet["I12"].value.startswith("⟦长文本预览")
assert sheet.column_dimensions["A"].hidden
assert str(sheet.freeze_panes) == "D7"
assert "C2:I2" in {str(r) for r in sheet.merged_cells.ranges}
assert wb["__PureProject"].sheet_state == "hidden"
meta = list(wb["__PureProject"].values)
assert meta[0][:3] == ("PureProject", "1", "2")
position = 1
payloads = []
while position < len(meta):
    _, identity, sheet_name, checksum, count = meta[position][:5]
    position += 1
    payload = base64.b64decode("".join(meta[position + n][2] for n in range(int(count))))
    assert hashlib.sha256(payload).hexdigest().upper() == checksum
    model = json.loads(payload)
    assert identity == model["id"] and sheet_name in wb.sheetnames
    payloads.append(model)
    position += int(count)
assert len(payloads[0]["tasks"][0]["description"]) == 90000
assert not payloads[0]["sync_enabled"] and payloads[0]["storage"] == "unknown-storage"
tree = ET.parse(root / "sample.mm")
assert tree.getroot().attrib["version"] == "1.0.1"
project_nodes = tree.getroot().find("node").findall("node")
assert len(project_nodes) == 2
for index, node in enumerate(project_nodes):
    attrs = {a.attrib["NAME"]: a.attrib["VALUE"] for a in node.findall("attribute")}
    payload = base64.b64decode("".join(attrs[f"pp:data:{n:06d}"] for n in range(int(attrs["pp:chunks"]))))
    assert hashlib.sha256(payload).hexdigest().upper() == attrs["pp:sha256"]
    assert json.loads(payload) == payloads[index]
with zipfile.ZipFile(root / "sample.pureproject") as z:
    manifest = json.loads(z.read("manifest.json"))
    assert manifest["Format"] == "PureProject" and manifest["Version"] == 1
    for index, entry in enumerate(manifest["Projects"]):
        payload = z.read(entry["Entry"])
        assert hashlib.sha256(payload).hexdigest().upper() == entry["Sha256"]
        assert json.loads(payload) == payloads[index]
result = {"success": True, "reader": "openpyxl " + openpyxl.__version__ + " / ElementTree / zipfile", "projects": len(payloads), "checks": ["typed dates", "literal formula-like text", "hidden metadata", "freeze panes", "merged headings", "payload SHA256", "equal canonical payloads in all formats"]}
(root / "independent-result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(result, ensure_ascii=False))
