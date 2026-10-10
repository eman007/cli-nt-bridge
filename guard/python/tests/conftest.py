import sys
from pathlib import Path

# guard/python holds the nt8guard_backup package; tests import it directly.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
