"""一鍵跑完五階段。--from 指定起點；每階段本身可續跑，所以中斷後重跑同一指令即可。"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline import clean, embed, fetch_civitai, load, structure  # noqa: E402

STAGES = ["fetch", "clean", "structure", "embed", "load"]


def stages_from(start: str) -> list[str]:
    return STAGES[STAGES.index(start):]


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Civitai → pgvector 全管線")
    ap.add_argument("--from", dest="start", choices=STAGES, default="fetch")
    ap.add_argument("--max-items", type=int, default=3000, help="fetch 階段本次最多新增筆數")
    ap.add_argument("--max-records", type=int, default=None, help="structure 階段本次最多處理筆數")
    ap.add_argument("--reindex", action="store_true", help="embed 階段全部重算")
    args = ap.parse_args(argv)

    for stage in stages_from(args.start):
        print(f"=== {stage} ===")
        if stage == "fetch":
            fetch_civitai.main(["--max-items", str(args.max_items)])
        elif stage == "clean":
            clean.main([])
        elif stage == "structure":
            structure.main([] if args.max_records is None else ["--max-records", str(args.max_records)])
        elif stage == "embed":
            embed.main(["--reindex"] if args.reindex else [])
        elif stage == "load":
            load.main([])


if __name__ == "__main__":
    main()
