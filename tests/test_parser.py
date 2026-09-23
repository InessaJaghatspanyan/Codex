from pathlib import Path

from labor_rag.parser import MAX_CHUNK_CHARS, clean_pdf_pages, load_corpus, split_articles

FIXTURE = Path(__file__).parent / "fixtures" / "sample_code.txt"


def test_splits_articles_with_structure():
    chunks = load_corpus([FIXTURE])
    by_article = {c.article: c for c in chunks if c.article}
    assert list(by_article) == ["1", "2", "3", "3.1", "4", "5"]
    assert chunks[0].article is None  # preamble line
    assert by_article["3"].title == "Աշխատանքային պայմանագրի կնքումը"
    assert by_article["3"].chapter == "Գլուխ 2. ԱՇԽԱՏԱՆՔԱՅԻՆ ՊԱՅՄԱՆԱԳԻՐ"
    assert by_article["1"].section == "Բաժին 1. ԸՆԴՀԱՆՈՒՐ ԴՐՈՒՅԹՆԵՐ"
    # Title on the line after the heading.
    assert by_article["4"].title == "Ամենամյա արձակուրդ"
    assert "ամենամյա վճարովի արձակուրդ" in by_article["4"].text


def test_english_and_russian_headings():
    en = split_articles("Article 12. Working time\nNormal hours are 40 per week.")
    ru = split_articles("Статья 12. Рабочее время\nНормальная продолжительность 40 часов.")
    assert en[0].article == ru[0].article == "12"
    assert en[0].title == "Working time"


def test_long_article_split_into_parts():
    body = "\n".join(f"{n}. " + "բառ " * 400 for n in range(1, 8))
    chunks = split_articles("Հոդված 7. Երկար հոդված\n" + body)
    assert len(chunks) > 1
    assert all(c.article == "7" for c in chunks)
    assert [c.part for c in chunks] == list(range(1, len(chunks) + 1))
    assert all(len(c.text) < MAX_CHUNK_CHARS * 2 for c in chunks)
    assert len({c.id for c in chunks}) == len(chunks)


def test_clean_pdf_pages_strips_footers_and_unwraps():
    footer = "ՀԱՅԱՍՏԱՆԻ ՀԱՆՐԱՊԵՏՈՒԹՅԱՆ ԱՇԽԱՏԱՆՔԱՅԻՆ ՕՐԵՆՍԳԻՐՔ\n© 1996 - 2026, PDF 23.09.2026"
    pages = [
        "01.01.2026 - 01.01.2027\n \nՀոդված 1. Առաջին\n \n1. Սա երկար\nտող է:\n"
        "ՀՀ Ազգային Ժողով, Օրենսգիրք\nԸնդունվել է. 09.11.2004\n" + footer + "\n1",
        "2. Երկրորդ մասը\nշարունակվում է ( այսուհետ` X) 1- ին մասով:\n \n" + footer + "\n2",
        "Հոդված 2. Երկրորդ\n \nՏեքստ:\n" + footer + "\n3",
    ]
    chunks = split_articles(clean_pdf_pages(pages))
    assert "Ընդունվել է. 09.11.2004" in chunks[0].text  # metadata moved to preamble
    art1 = chunks[1]
    assert art1.text == (
        "1. Սա երկար տող է:\n2. Երկրորդ մասը շարունակվում է (այսուհետ` X) 1-ին մասով:"
    )
    assert chunks[2].article == "2" and chunks[2].text == "Տեքստ:"
    assert not any("©" in c.text for c in chunks)
