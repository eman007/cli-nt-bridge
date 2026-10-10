"""NT8 Guard - put strategies back on their charts after an Id change. See nt8guard_backup/relink.py."""
import sys
from pathlib import Path

# The embeddable Python ignores the script's folder (its ._pth file fixes sys.path), so add it here.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from nt8guard_backup.relink import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
