"""Regenerate tiny, deterministic, synthetic legacy interchange regression inputs.

Python standard library only. No production exporter, audit artifacts, native apps,
network, absolute paths or user data are used. The fixed files are test inputs, not
evidence that a native application has opened or saved a file.
"""
import base64
import copy
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent
S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
P = "http://schemas.openxmlformats.org/package/2006/relationships"
ET.register_namespace("", S)
ET.register_namespace("r", R)


def project():
    return {
        "id": "portable-legacy", "name": "名称\r\n次行", "description": "首段\r\n二段\r三段\n" + "完整正文" * 60,
        "default_task_group_id": "g", "sync_enabled": False,
        "task_groups": [{"id": "g", "name": "组\r\n次行", "initial_status_id": "todo", "completion_status_id": "done", "statuses": [
            {"id": "todo", "name": "待办", "color": "#6b7280", "category": "todo"},
            {"id": "done", "name": "完成", "color": "#6b7280", "category": "done"}]}],
        "tasks": [{"id": "t", "title": "=1+1", "task_group_id": "g", "status_id": "todo", "priority": "medium",
                   "description": "首段\r\n二段\r三段\n" + "任务正文" * 60,
                   "due_date": None, "due_time": "", "subtasks": [{"id": "s", "title": "控制\u0001 字面⟦U+0001⟧", "done": False}],
                   "comments": [{"id": "c", "content": "保留评论", "created_at": ""}],
                   "future_task": {"array": [1, "二", None]}}],
        "_pm_extensions": {"portable_extension": {"enabled": True}},
        "future_project": {"text": "保留未知属性"},
    }


def payload(p):
    raw = json.dumps(p, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return base64.b64encode(raw).decode("ascii"), hashlib.sha256(raw).hexdigest().upper()


def preview(text):
    return text if len(text) <= 200 else "⟦长文本预览；未改保留全文，编辑则替换⟧\n" + text[:70] + "…"


def excel_text(text):
    text = re.sub(r"_x[0-9a-fA-F]{4}_", lambda m: "_x005F_" + m[0][1:], text)
    return re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f\ufffe\uffff]", lambda m: f"_x{ord(m[0]):04X}_", text)


def worksheet(rows, legacy_newlines=False):
    sheet = ET.Element(f"{{{S}}}worksheet")
    data = ET.SubElement(sheet, f"{{{S}}}sheetData")
    for index, values in enumerate(rows, 1):
        row = ET.SubElement(data, f"{{{S}}}row", r=str(index))
        for col, value in enumerate(values):
            if value == "":
                continue
            if legacy_newlines:
                value = value.replace("\r\n", "\n").replace("\r", "\n")
            cell = ET.SubElement(row, f"{{{S}}}c", r=f"{chr(65 + col)}{index}", t="inlineStr")
            text = ET.SubElement(ET.SubElement(cell, f"{{{S}}}is"), f"{{{S}}}t")
            text.text = excel_text(value)
    return ET.tostring(sheet, encoding="utf-8", xml_declaration=True)


def workbook(name, edited_literal=False):
    p = project()
    encoded, checksum = payload(p)
    task = p["tasks"][0]
    rows = [["help", "synthetic legacy regression fixture"], ["project", p["id"], p["name"]], ["description", "", preview(p["description"])],
            ["类型", "ID（保留）", "名称 / 任务标题", "任务组 ID", "状态 ID / 状态类别", "优先级", "截止日期", "截止时间", "任务描述"],
            ["group", "g", p["task_groups"][0]["name"]], ["status", "todo", "待办", "g", "todo"], ["status", "done", "完成", "g", "done"],
            ["task", "t", task["title"], "g", "todo", "medium", "", "", "_x0041_" * 4000 if edited_literal else preview(task["description"])]]
    parts = {
        "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
        "_rels/.rels": f'<Relationships xmlns="{P}"><Relationship Id="rId1" Type="{R}/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": f'<workbook xmlns="{S}" xmlns:r="{R}"><sheets><sheet name="项目" sheetId="1" r:id="rId1"/><sheet name="__PureProject" sheetId="2" state="hidden" r:id="rId2"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": f'<Relationships xmlns="{P}"><Relationship Id="rId1" Type="{R}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{R}/worksheet" Target="worksheets/sheet2.xml"/></Relationships>',
        "xl/worksheets/sheet1.xml": worksheet(rows, legacy_newlines=True),
        "xl/worksheets/sheet2.xml": worksheet([["PureProject", "1", "1"], ["project", p["id"], "项目", checksum, "1"], ["data", "0", encoded]]),
    }
    with zipfile.ZipFile(ROOT / name, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for entry, value in parts.items():
            info = zipfile.ZipInfo(entry, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, value)


def legacy_text(text):
    return re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f\ufffe\uffff]", lambda m: f"⟦U+{ord(m[0]):04X}⟧", text)


def attribute(node, name, value):
    return ET.SubElement(node, "attribute", NAME=name, VALUE=legacy_text(value))


def business_node(parent, kind, identity, title):
    node = ET.SubElement(parent, "node", TEXT=legacy_text(title), ID=identity, FOLDED="true")
    attribute(node, "pp:type", kind)
    attribute(node, "pp:id", identity)
    return node


def mindmap():
    p = project()
    p["description"] = "项目说明：中文、English、emoji 🌱。"
    tree = ET.Element("map", version="1.0.1")
    root = ET.SubElement(tree, "node", TEXT="PureProject 项目", ID="root")
    attribute(root, "pp:format", "PureProject/1")
    attribute(root, "pp:projects", "1")
    node = business_node(root, "project", p["id"], p["name"])
    encoded, checksum = payload(p)
    for name, value in [("pp:sha256", checksum), ("pp:chunks", "1"), ("pp:description", p["description"]), ("pp:data:000000", encoded)]:
        attribute(node, name, value)
    group = business_node(node, "group", "g", p["task_groups"][0]["name"])
    todo = business_node(group, "status", "todo", "待办")
    business_node(group, "status", "done", "完成")
    task = business_node(todo, "task", "t", "=1+1")
    for name, value in [("pp:description", preview(p["tasks"][0]["description"])), ("pp:priority", "medium"), ("pp:due_date", ""), ("pp:due_time", "")]:
        attribute(task, name, value)
    subtask = business_node(task, "subtask", "s", p["tasks"][0]["subtasks"][0]["title"])
    attribute(subtask, "pp:index", "0")
    attribute(subtask, "pp:done", "false")
    (ROOT / "legacy-v1.mm").write_bytes(ET.tostring(tree, encoding="utf-8", xml_declaration=True))
    damaged = copy.deepcopy(tree)
    for a in damaged.iter("attribute"):
        if a.get("NAME") == "pp:description":
            a.set("VALUE", a.get("VALUE").replace("🌱", "\uf331"))
    (ROOT / "legacy-v1-damaged.mm").write_bytes(ET.tostring(damaged, encoding="utf-8", xml_declaration=True))


workbook("legacy-newlines.xlsx")
workbook("legacy-escaped-28000.xlsx", edited_literal=True)
mindmap()
names = ["legacy-newlines.xlsx", "legacy-escaped-28000.xlsx", "legacy-v1.mm", "legacy-v1-damaged.mm"]
manifest = {name: {"bytes": (ROOT / name).stat().st_size, "sha256": hashlib.sha256((ROOT / name).read_bytes()).hexdigest().upper()} for name in names}
(ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(json.dumps(manifest, indent=2))
