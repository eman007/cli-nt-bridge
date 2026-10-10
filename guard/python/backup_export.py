"""NT8 Guard - save every strategy's settings. See nt8guard_backup/export.py."""
import sys
from pathlib import Path

# The embeddable Python ignores the script's folder (its ._pth file fixes sys.path), so add it here.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from nt8guard_backup.export import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
