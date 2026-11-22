"""Fetches the plain text of Wikipedia articles on a fixed list of topics in 56 languages: the external corpus of the benchmark report.

Usage:
    python fetch_wikipedia.py <output folder>

Writes one UTF-8 file per article, <lang>_<nn>.txt (at most MAX_CHARS characters of the article), and manifest.tsv with the language, file,
title, revision id and time of the revision of each article and the date of the fetch. Files that are already there are not fetched again, so an
interrupted run can be started again.

The requests are paced for the rate limit of the Wikipedia API (one every PAUSE seconds, with waits on errors); a full run takes about 15 minutes.

The text is Wikipedia's, under CC BY-SA (https://creativecommons.org/licenses/by-sa/4.0/), which is why it is not in the repository. Articles
change all the time and the API gives the current revision only, so a fetch on another day gives other text and other numbers; the manifest
records which revisions a set was made of.
"""
import argparse
import datetime
import json
import os
import time
import urllib.parse
import urllib.request

TOPICS = ["Water", "Computer", "Music", "City", "Football", "Coffee", "Internet", "Railway"]

LANGS = ["en", "fr", "de", "es", "pt", "it", "nl", "sv", "da", "fi", "is", "pl", "cs", "sk", "hu", "ro", "lt", "lv", "hr", "sl", "tr", "vi", "id",
         "ru", "uk", "bg", "sr", "kk", "mn", "el", "hy", "ka", "he", "ar", "fa", "ur", "hi", "mr", "ne", "bn", "pa", "gu", "ta", "te", "kn", "ml",
         "si", "th", "lo", "km", "my", "bo", "am", "zh", "ja", "ko"]

MAX_CHARS = 30000
MIN_CHARS = 500
PAUSE = 1.5
AGENT = "text-encoding-benchmark-corpus/1.0 (fetches a few articles for an encoding size test)"


def get(url):
    """Gets a JSON response, with retries that honour Retry-After."""
    request = urllib.request.Request(url, headers={"User-Agent": AGENT})
    last = None

    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.loads(response.read().decode("utf-8"))
        except Exception as error:  # noqa: BLE001 - any failure is retried, then reported
            last = error
            wait = 20 + attempt * 20
            headers = getattr(error, "headers", None)

            if headers is not None and headers.get("Retry-After", "").isdigit():
                wait = max(wait, int(headers.get("Retry-After")) + 1)

            time.sleep(wait)

    print("  failed:", url[:100], last)
    return None


def titles_by_language():
    """The titles of the topics in each language, from the language links of the English articles."""
    titles = {lang: [] for lang in LANGS}
    titles["en"] = list(TOPICS)

    for topic in TOPICS:
        cont = ""

        while True:
            data = get("https://en.wikipedia.org/w/api.php?action=query&format=json&redirects=1&prop=langlinks&lllimit=500&titles="
                       + urllib.parse.quote(topic) + cont)

            if data is None:
                break

            for page in data.get("query", {}).get("pages", {}).values():
                for link in page.get("langlinks", []):
                    if link["lang"] in titles and link["lang"] != "en":
                        titles[link["lang"]].append(link["*"])

            if "continue" not in data:
                break

            cont = "&llcontinue=" + urllib.parse.quote(data["continue"]["llcontinue"])

        time.sleep(PAUSE)

    return titles


def main():
    parser = argparse.ArgumentParser(description="Fetches the Wikipedia corpus of the benchmark report.")
    parser.add_argument("output", help="the folder to write the articles to")
    out = parser.parse_args().output
    os.makedirs(out, exist_ok=True)

    today = datetime.date.today().isoformat()
    manifest_path = os.path.join(out, "manifest.tsv")
    new_manifest = not os.path.exists(manifest_path)

    with open(manifest_path, "a", encoding="utf-8", newline="\n") as manifest:
        if new_manifest:
            manifest.write("lang\tfile\ttitle\trevision\trevision_time\tfetched\tchars\n")

        titles = titles_by_language()
        total = 0

        for lang in LANGS:
            count = 0
            chars = 0

            for title in titles[lang]:
                url = (f"https://{lang}.wikipedia.org/w/api.php?action=query&format=json&redirects=1&prop=extracts|revisions"
                       "&explaintext=1&exlimit=1&rvprop=ids|timestamp&titles=" + urllib.parse.quote(title))
                data = get(url)
                time.sleep(PAUSE)

                if data is None:
                    continue

                for page in data.get("query", {}).get("pages", {}).values():
                    text = page.get("extract", "")

                    if len(text) < MIN_CHARS:
                        continue

                    count += 1
                    name = f"{lang}_{count:02d}.txt"
                    path = os.path.join(out, name)
                    text = text[:MAX_CHARS]
                    chars += len(text)

                    if not os.path.exists(path):
                        with open(path, "w", encoding="utf-8", newline="\n") as f:
                            f.write(text)

                        revision = (page.get("revisions") or [{}])[0]
                        manifest.write(f"{lang}\t{name}\t{page.get('title', title)}\t{revision.get('revid', '')}\t"
                                       f"{revision.get('timestamp', '')}\t{today}\t{len(text)}\n")
                        manifest.flush()

            total += chars
            print(f"{lang}: {count} articles, {chars} chars", flush=True)

    print("total chars:", total)


if __name__ == "__main__":
    main()
