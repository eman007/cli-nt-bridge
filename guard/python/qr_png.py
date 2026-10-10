"""NT8 Guard - a QR code for the control panel's phone-alert address.

Prints the PNG, base64-encoded, for the text in the NT8GUARD_QR_TEXT environment variable. The text comes
through the environment and not the command line: other programs can read a command line, and this
address is the password to the alert channel. Dark modules on white with the standard quiet zone, so
every phone camera reads it.

Exit 0 = printed; 1 = no text.
"""
import base64
import io
import os
import sys
from pathlib import Path

# The embeddable Python ignores the script's folder (its ._pth file fixes sys.path), so add it here.
sys.path.insert(0, str(Path(__file__).resolve().parent / "vendor"))
import segno  # noqa: E402


def main() -> int:
    text = os.environ.get("NT8GUARD_QR_TEXT", "")
    if not text:
        print("ERROR: NT8GUARD_QR_TEXT is empty - nothing to draw", file=sys.stderr)
        return 1
    buf = io.BytesIO()
    segno.make(text, error="m", micro=False).save(buf, kind="png", scale=8, border=4,
                                                    dark="#0b0c0f", light="#ffffff")
    print(base64.b64encode(buf.getvalue()).decode("ascii"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
