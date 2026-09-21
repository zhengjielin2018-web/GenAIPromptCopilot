import sys
from pathlib import Path

# 讓 tests 能 import pipeline.*（從 scripts/ 執行 pytest 時）
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
