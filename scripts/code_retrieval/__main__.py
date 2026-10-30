"""Entry point: python -m scripts.code_retrieval <stage> ..."""

import json
import sys

from .cli import main


def run(argv=None):
    try:
        return main(argv)
    except (ValueError, OSError) as exc:
        print(json.dumps({"status": "FAILED", "error": str(exc)}, indent=2, sort_keys=True))
        return None


if __name__ == "__main__":
    result = run(sys.argv[1:])
    if result is not None:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        raise SystemExit(2)
