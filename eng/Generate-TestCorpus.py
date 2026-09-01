from __future__ import annotations

import io
import math
import sys
from pathlib import Path

from PIL import Image
from pypdf import PdfReader, PdfWriter
from pypdf.generic import DictionaryObject, NameObject, TextStringObject
from reportlab.lib.colors import Color, black, blue, red
from reportlab.lib.pagesizes import A4, LETTER, landscape
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.cidfonts import UnicodeCIDFont
from reportlab.pdfgen import canvas
from reportlab.lib.pdfencrypt import StandardEncryption


OUTPUT_NAMES = (
    "synthetic-vector-small.pdf",
    "synthetic-photo-scan.pdf",
    "synthetic-cjk-font-heavy.pdf",
    "synthetic-mixed-orientation-links-forms-outlines.pdf",
    "synthetic-encrypted.pdf",
    "synthetic-1000-pages.pdf",
    "synthetic-10000-pages.pdf",
    "synthetic-huge-mediabox.pdf",
    "synthetic-corrupt.pdf",
    "synthetic-parser-stress.pdf",
    "synthetic-1gb-padded.pdf",
)


def new_canvas(path: Path, pagesize=LETTER, encrypt=None) -> canvas.Canvas:
    return canvas.Canvas(
        str(path),
        pagesize=pagesize,
        pageCompression=1,
        invariant=1,
        encrypt=encrypt,
    )


def vector_small(path: Path) -> None:
    c = new_canvas(path)
    width, height = LETTER
    for page in range(3):
        c.setTitle("ElliePdf synthetic vector fixture")
        c.setFont("Helvetica-Bold", 22)
        c.drawString(54, height - 72, f"Vector page {page + 1}")
        c.setFont("Helvetica", 11)
        c.drawString(54, height - 96, "Selectable text: ElliePdf quick brown fox 0123456789")
        for index in range(24):
            hue = index / 24
            c.setFillColor(Color(hue, 0.35, 1 - hue, alpha=0.75))
            x = 54 + (index % 8) * 64
            y = height - 160 - (index // 8) * 64
            c.circle(x, y, 22 + page * 2, fill=1, stroke=0)
        c.setStrokeColor(black)
        c.line(54, 90, width - 54, height - 220)
        c.showPage()
    c.save()


def photo_scan(path: Path) -> None:
    image = Image.new("RGB", (1024, 768))
    pixels = image.load()
    for y in range(image.height):
        for x in range(image.width):
            pixels[x, y] = (
                (x * 255) // (image.width - 1),
                (y * 255) // (image.height - 1),
                ((x ^ y) * 17) & 0xFF,
            )
    encoded = io.BytesIO()
    image.save(encoded, format="PNG", optimize=False)
    encoded.seek(0)
    reader = ImageReader(encoded)

    c = new_canvas(path, pagesize=A4)
    width, height = A4
    for page in range(3):
        c.drawImage(reader, 24, 80, width=width - 48, height=height - 120, mask="auto")
        c.setFillColor(black)
        c.setFont("Helvetica", 9)
        c.drawString(24, 48, f"Synthetic raster scan {page + 1}; no external image content")
        c.showPage()
    c.save()


def cjk_font_heavy(path: Path) -> None:
    pdfmetrics.registerFont(UnicodeCIDFont("STSong-Light"))
    lines = (
        "中文测试文档：快速、清晰、可选择的文本。",
        "日本語の合成テキスト — PDF リーダー検証。",
        "한국어 합성 텍스트와 숫자 0123456789.",
    )
    c = new_canvas(path, pagesize=A4)
    width, height = A4
    for page in range(2):
        c.setFont("STSong-Light", 13)
        y = height - 54
        for row in range(48):
            c.drawString(42, y, f"{row + 1:02d} {lines[row % len(lines)]}")
            y -= 15
        c.showPage()
    c.save()


def mixed_features(path: Path) -> None:
    c = new_canvas(path, pagesize=LETTER)
    c.setTitle("ElliePdf mixed semantic fixture")
    c.setAuthor("ElliePdf synthetic corpus generator")
    c.setSubject("Links, forms, outlines, rotations, metadata and text")
    for page in range(8):
        size = landscape(LETTER) if page % 3 == 1 else LETTER
        c.setPageSize(size)
        width, height = size
        bookmark = f"page-{page + 1}"
        c.bookmarkPage(bookmark)
        c.addOutlineEntry(f"Synthetic section {page + 1}", bookmark, level=0)
        c.setFont("Helvetica-Bold", 20)
        c.drawString(48, height - 60, f"Mixed feature page {page + 1}")
        c.setFont("Helvetica", 11)
        c.drawString(48, height - 88, "Selectable semantic text, internal navigation, and bounded external links.")
        c.setFillColor(blue)
        c.drawString(48, height - 112, "https://example.invalid/elliepdf")
        c.linkURL(
            "https://example.invalid/elliepdf",
            (48, height - 116, 250, height - 100),
            relative=0,
        )
        c.drawString(48, height - 136, "javascript:alert('blocked')")
        c.linkURL(
            "javascript:alert('blocked')",
            (48, height - 140, 230, height - 124),
            relative=0,
        )
        c.setFillColor(red)
        c.drawString(260, height - 112, f"Jump to page {(page + 2) if page < 7 else 1}")
        c.linkRect(
            "",
            f"page-{(page + 2) if page < 7 else 1}",
            (260, height - 116, 380, height - 100),
            relative=0,
            thickness=0,
        )
        c.setFillColor(black)
        if page == 0:
            c.acroForm.textfield(
                name="text_field",
                value="Synthetic text value",
                x=48,
                y=height - 190,
                width=260,
                height=24,
                borderStyle="inset",
                forceBorder=True,
            )
        elif page == 1:
            c.acroForm.checkbox(
                name="checkbox_field",
                checked=False,
                x=48,
                y=height - 190,
                size=18,
                buttonStyle="check",
                forceBorder=True,
            )
        elif page == 2:
            c.acroForm.choice(
                name="combo_field",
                value="Beta",
                options=["Alpha", "Beta", "Gamma"],
                x=48,
                y=height - 190,
                width=180,
                height=24,
                borderStyle="solid",
                forceBorder=True,
            )
        elif page == 3:
            c.acroForm.listbox(
                name="list_field",
                value="South",
                options=["North", "South", "East", "West"],
                x=48,
                y=height - 228,
                width=180,
                height=64,
                borderStyle="solid",
                forceBorder=True,
            )
        elif page == 4:
            c.acroForm.textfield(
                name="readonly_field",
                value="Read only synthetic value",
                x=48,
                y=height - 190,
                width=260,
                height=24,
                borderStyle="inset",
                fieldFlags="readOnly",
                forceBorder=True,
            )
        elif page == 5:
            c.acroForm.textfield(
                name="unsafe_text_field",
                value="Unsafe synthetic value",
                x=48,
                y=height - 190,
                width=260,
                height=24,
                borderStyle="inset",
                forceBorder=True,
            )
        elif page == 6:
            c.acroForm.radio(
                name="radio_field",
                value="radio_a",
                selected=True,
                x=48,
                y=height - 190,
                size=18,
                forceBorder=True,
            )
            c.acroForm.radio(
                name="radio_field",
                value="radio_b",
                selected=False,
                x=84,
                y=height - 190,
                size=18,
                forceBorder=True,
            )
        else:
            c.acroForm.textfield(
                name="required_text_field",
                value="Required synthetic value",
                x=48,
                y=height - 190,
                width=260,
                height=24,
                borderStyle="inset",
                fieldFlags="required",
                forceBorder=True,
            )
        c.showPage()
    c.save()
    apply_mixed_feature_post_processing(path)


def apply_mixed_feature_post_processing(path: Path) -> None:
    reader = PdfReader(str(path))
    writer = PdfWriter()
    writer.clone_document_from_reader(reader)

    unsafe_widget = None
    checkbox_widget = None
    for page in writer.pages:
        annotations = page.get("/Annots") or []
        for annotation_ref in annotations:
            annotation = annotation_ref.get_object()
            if annotation.get("/Subtype") != "/Widget":
                continue
            if annotation.get("/T") == "checkbox_field":
                checkbox_widget = annotation
            if annotation.get("/T") == "unsafe_text_field":
                unsafe_widget = annotation
        if unsafe_widget is not None and checkbox_widget is not None:
            break

    if unsafe_widget is None:
        raise RuntimeError("unsafe_text_field widget was not generated.")
    if checkbox_widget is None:
        raise RuntimeError("checkbox_field widget was not generated.")

    checkbox_widget[NameObject("/AS")] = NameObject("/Off")

    unsafe_widget[NameObject("/AA")] = DictionaryObject(
        {
            NameObject("/K"): DictionaryObject(
                {
                    NameObject("/S"): NameObject("/JavaScript"),
                    NameObject("/JS"): TextStringObject("app.alert('unsafe synthetic widget');"),
                }
            )
        }
    )

    with path.open("wb") as stream:
        writer.write(stream)


def encrypted(path: Path) -> None:
    encryption = StandardEncryption(
        userPassword="ellie-test",
        ownerPassword="ellie-owner",
        canPrint=1,
        canModify=0,
        canCopy=1,
        canAnnotate=0,
        strength=128,
    )
    c = new_canvas(path, pagesize=LETTER, encrypt=encryption)
    for page in range(2):
        c.setFont("Helvetica-Bold", 18)
        c.drawString(54, 720, f"Encrypted synthetic page {page + 1}")
        c.setFont("Helvetica", 11)
        c.drawString(54, 690, "User password: ellie-test (fixture-only credential)")
        c.showPage()
    c.save()


def long_document(path: Path, page_count: int) -> None:
    c = new_canvas(path, pagesize=LETTER)
    for page in range(page_count):
        c.setFont("Helvetica", 8)
        c.drawString(24, 24, f"Synthetic page {page + 1} of {page_count}")
        if page % 100 == 0:
            c.setFont("Helvetica-Bold", 16)
            c.drawString(48, 720, f"Checkpoint {page + 1}")
        c.showPage()
    c.save()


def huge_mediabox(path: Path) -> None:
    c = new_canvas(path, pagesize=(1_000_000, 1_000_000))
    c.setFont("Helvetica", 12)
    c.drawString(10, 10, "Huge MediaBox resource-limit fixture")
    c.showPage()
    c.save()


def corrupt(path: Path) -> None:
    path.write_bytes(
        b"%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
        b"2 0 obj\n<< /Type /Pages /Count 999999 /Kids ["
    )


def parser_stress(path: Path) -> None:
    c = new_canvas(path, pagesize=A4)
    width, height = A4
    for page in range(20):
        c.setFont("Courier", 7)
        for row in range(70):
            value = (page + 1) * (row + 3)
            c.drawString(18, height - 18 - row * 11, f"{value:06d} ()[]{{}} /Name % escaped \\ text")
        c.setStrokeColor(red)
        for index in range(40):
            angle = index * math.pi / 20
            c.line(
                width / 2,
                height / 2,
                width / 2 + math.cos(angle) * 180,
                height / 2 + math.sin(angle) * 180,
            )
        c.showPage()
    c.save()


def one_gigabyte_padded(path: Path) -> None:
    """Create a valid tiny PDF with deterministic zero padding to exactly 1 GiB.

    The fixture is intentionally opt-in: generating it consumes about 1 GiB of
    disk space and is suitable for controlled performance runs only.
    """
    temporary = path.with_suffix(".base.pdf")
    vector_small(temporary)
    data = temporary.read_bytes()
    temporary.unlink()
    target_size = 1_073_741_824
    if len(data) >= target_size:
        raise RuntimeError("base PDF unexpectedly exceeds the 1-GiB target")
    with path.open("wb") as stream:
        stream.write(data)
        stream.seek(target_size - 1)
        stream.write(b"\0")


def main() -> int:
    if len(sys.argv) not in (2, 3) or (len(sys.argv) == 3 and sys.argv[2] != "--include-1gb"):
        print("usage: Generate-TestCorpus.py OUTPUT_DIRECTORY [--include-1gb]", file=sys.stderr)
        return 2

    output = Path(sys.argv[1]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    vector_small(output / OUTPUT_NAMES[0])
    photo_scan(output / OUTPUT_NAMES[1])
    cjk_font_heavy(output / OUTPUT_NAMES[2])
    mixed_features(output / OUTPUT_NAMES[3])
    encrypted(output / OUTPUT_NAMES[4])
    long_document(output / OUTPUT_NAMES[5], 1_000)
    long_document(output / OUTPUT_NAMES[6], 10_000)
    huge_mediabox(output / OUTPUT_NAMES[7])
    corrupt(output / OUTPUT_NAMES[8])
    parser_stress(output / OUTPUT_NAMES[9])
    generated = len(OUTPUT_NAMES) - 1
    if len(sys.argv) == 3:
        one_gigabyte_padded(output / OUTPUT_NAMES[10])
        generated += 1
    print(f"generated {generated} deterministic fixtures in {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
