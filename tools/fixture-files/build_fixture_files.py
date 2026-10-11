#!/usr/bin/env python3
"""Builds the binary Office/PDF files of fixtures/company.yaml from plain-text sources.

Each file `sources/<name>.<ext>.txt` becomes `fixtures/files/company/<name>.<ext>`.
Output is reproducible: document metadata and zip timestamps are fixed.

Usage (needs python-docx, reportlab, openpyxl, python-pptx; see requirements.txt):
    python tools/fixture-files/build_fixture_files.py            # build everything
    python tools/fixture-files/build_fixture_files.py --verify   # also read each file back and print the extracted word count

Source format
  docx / pdf : "# Title", "## Heading", "### Subheading", "- bullet", "| a | b |" table rows (first row is the header),
               other lines are paragraphs (consecutive lines are joined; a blank line ends a paragraph).
  xlsx       : "## Sheet: <name>" starts a sheet; each following line is a row of cells separated by " | ".
               Cells that look like numbers are stored as numbers.
  pptx       : slides are separated by a line "---"; the first "# " line is the slide title, "- " lines are bullets.
"""
import argparse
import datetime as dt
import io
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCES = Path(__file__).resolve().parent / "sources"
OUT = ROOT / "fixtures" / "files" / "company"
FIXED = dt.datetime(2026, 1, 15, 9, 0, 0)
AUTHOR = "Contoso Software (mock fixture)"


def parse_blocks(text):
    blocks, para, table = [], [], []

    def flush():
        nonlocal para, table
        if para:
            blocks.append(("para", " ".join(para)))
            para = []
        if table:
            rows = [r for r in table if not re.fullmatch(r"\|[\s:|-]+\|", r)]
            blocks.append(("table", [[c.strip() for c in r.strip().strip("|").split("|")] for r in rows]))
            table = []

    for line in text.splitlines():
        s = line.rstrip()
        if not s.strip():
            flush()
        elif s.startswith("|"):
            if para:
                flush()
            table.append(s)
        elif s.startswith("### "):
            flush(); blocks.append(("h3", s[4:]))
        elif s.startswith("## "):
            flush(); blocks.append(("h2", s[3:]))
        elif s.startswith("# "):
            flush(); blocks.append(("h1", s[2:]))
        elif s.startswith("- "):
            flush(); blocks.append(("bullet", s[2:]))
        else:
            if table:
                flush()
            para.append(s.strip())
    flush()
    return blocks


def normalize_zip(path):
    """Rewrite an OOXML zip with fixed timestamps so rebuilding yields identical bytes."""
    with zipfile.ZipFile(path) as zin:
        entries = [(i.filename, zin.read(i.filename)) for i in zin.infolist()]
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as zout:
        for name, data in entries:
            if name == "docProps/core.xml":
                # openpyxl stamps the save time into dcterms:modified; pin it.
                data = re.sub(rb"(<dcterms:modified[^>]*>)[^<]*", rb"\g<1>2026-01-15T09:00:00Z", data)
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 15, 9, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            zout.writestr(info, data)
    Path(path).write_bytes(buffer.getvalue())


def build_docx(text, dest):
    from docx import Document

    doc = Document()
    title = None
    for kind, value in parse_blocks(text):
        if kind == "h1":
            title = title or value
            doc.add_heading(value, level=0)
        elif kind in ("h2", "h3"):
            doc.add_heading(value, level=1 if kind == "h2" else 2)
        elif kind == "bullet":
            doc.add_paragraph(value, style="List Bullet")
        elif kind == "para":
            doc.add_paragraph(value)
        elif kind == "table":
            table = doc.add_table(rows=len(value), cols=max(len(r) for r in value))
            table.style = "Table Grid"
            for r, row in enumerate(value):
                for c, cell in enumerate(row):
                    table.cell(r, c).text = cell
                    if r == 0:
                        for run in table.cell(r, c).paragraphs[0].runs:
                            run.bold = True
            doc.add_paragraph()
    props = doc.core_properties
    props.author = props.last_modified_by = AUTHOR
    props.title = title or dest.stem
    props.created = props.modified = FIXED
    props.revision = 1
    props.comments = ""
    doc.save(dest)
    normalize_zip(dest)


def xml_escape(value):
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def build_pdf(text, dest):
    from reportlab.lib import colors
    from reportlab.lib.pagesizes import A4
    from reportlab.lib.styles import getSampleStyleSheet
    from reportlab.lib.units import mm
    from reportlab.platypus import ListFlowable, ListItem, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

    styles = getSampleStyleSheet()
    flow, bullets, title = [], [], None

    def flush_bullets():
        nonlocal bullets
        if bullets:
            flow.append(ListFlowable([ListItem(Paragraph(xml_escape(b), styles["BodyText"])) for b in bullets], bulletType="bullet", start="-"))
            bullets = []

    for kind, value in parse_blocks(text):
        if kind != "bullet":
            flush_bullets()
        if kind == "h1":
            title = title or value
            flow.append(Paragraph(xml_escape(value), styles["Title"]))
        elif kind == "h2":
            flow.append(Paragraph(xml_escape(value), styles["Heading2"]))
        elif kind == "h3":
            flow.append(Paragraph(xml_escape(value), styles["Heading3"]))
        elif kind == "bullet":
            bullets.append(value)
        elif kind == "para":
            flow.append(Paragraph(xml_escape(value), styles["BodyText"]))
        elif kind == "table":
            data = [[Paragraph(xml_escape(c), styles["BodyText"]) for c in row] for row in value]
            t = Table(data, repeatRows=1)
            t.setStyle(TableStyle([("GRID", (0, 0), (-1, -1), 0.5, colors.grey), ("BACKGROUND", (0, 0), (-1, 0), colors.lightgrey), ("VALIGN", (0, 0), (-1, -1), "TOP")]))
            flow.extend([Spacer(1, 3 * mm), t, Spacer(1, 3 * mm)])
    flush_bullets()
    SimpleDocTemplate(str(dest), pagesize=A4, title=title or dest.stem, author=AUTHOR, subject="Contoso mock fixture", invariant=1,
                      leftMargin=20 * mm, rightMargin=20 * mm, topMargin=18 * mm, bottomMargin=18 * mm).build(flow)


def build_xlsx(text, dest):
    from openpyxl import Workbook
    from openpyxl.styles import Font

    wb = Workbook()
    wb.remove(wb.active)
    sheet = None
    for line in text.splitlines():
        if line.startswith("## Sheet:"):
            sheet = wb.create_sheet(line.split(":", 1)[1].strip())
            continue
        if not line.strip() or sheet is None:
            continue
        cells = []
        for raw in line.split(" | "):
            raw = raw.strip()
            cells.append(int(raw) if re.fullmatch(r"-?\d+", raw) else raw)
        sheet.append(cells)
        if sheet.max_row == 1:
            for cell in sheet[1]:
                cell.font = Font(bold=True)
    for ws in wb.worksheets:
        for col in ws.columns:
            ws.column_dimensions[col[0].column_letter].width = min(60, max(len(str(c.value or "")) for c in col) + 2)
    wb.properties.creator = wb.properties.lastModifiedBy = AUTHOR
    wb.properties.created = wb.properties.modified = FIXED
    wb.properties.title = dest.stem
    wb.save(dest)
    normalize_zip(dest)


def build_pptx(text, dest):
    from pptx import Presentation

    prs = Presentation()
    title_text = None
    for chunk in re.split(r"^---\s*$", text, flags=re.M):
        title, bullets = None, []
        for line in chunk.splitlines():
            if line.startswith("# ") and title is None:
                title = line[2:].strip()
            elif line.startswith("- "):
                bullets.append(line[2:].strip())
        if title is None:
            continue
        title_text = title_text or title
        slide = prs.slides.add_slide(prs.slide_layouts[1])
        slide.shapes.title.text = title
        body = slide.placeholders[1].text_frame
        body.text = bullets[0] if bullets else ""
        for bullet in bullets[1:]:
            body.add_paragraph().text = bullet
    props = prs.core_properties
    props.author = props.last_modified_by = AUTHOR
    props.title = title_text or dest.stem
    props.created = props.modified = FIXED
    props.revision = 1
    prs.save(dest)
    normalize_zip(dest)


BUILDERS = {".docx": build_docx, ".pdf": build_pdf, ".xlsx": build_xlsx, ".pptx": build_pptx}


def extract_text(path):
    ext = path.suffix.lower()
    if ext == ".docx":
        from docx import Document
        doc = Document(path)
        parts = [p.text for p in doc.paragraphs] + [c.text for t in doc.tables for r in t.rows for c in r.cells]
        return "\n".join(parts)
    if ext == ".pdf":
        from pypdf import PdfReader
        return "\n".join(page.extract_text() for page in PdfReader(path).pages)
    if ext == ".xlsx":
        from openpyxl import load_workbook
        wb = load_workbook(path, read_only=True)
        return "\n".join(ws.title + "\n" + "\n".join(" ".join(str(v) for v in row if v is not None) for row in ws.iter_rows(values_only=True)) for ws in wb.worksheets)
    from pptx import Presentation
    return "\n".join(s.text for slide in Presentation(path).slides for s in slide.shapes if s.has_text_frame)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--verify", action="store_true", help="read each generated file back and check key words")
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    failures = 0
    for source in sorted(SOURCES.glob("*.txt")):
        name = source.name[:-4]
        dest = OUT / name
        text = source.read_text(encoding="utf-8")
        BUILDERS[dest.suffix.lower()](text, dest)
        line = f"{name}: {dest.stat().st_size} bytes"
        if args.verify:
            extracted = re.sub(r"\s+", " ", extract_text(dest))
            words = re.findall(r"[A-Za-z0-9]{5,}", text.replace("## Sheet:", ""))
            missing = [w for w in dict.fromkeys(words) if w not in extracted]
            line += f", {len(extracted.split())} words extracted, {len(missing)} missing"
            failures += bool(missing)
            if missing:
                line += f" {missing[:5]}"
        print(line)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
