from datetime import datetime, timezone

def format_iso_date(epoch_ms):
    return datetime.fromtimestamp(epoch_ms / 1000, tz=timezone.utc).strftime("%Y-%m-%d")
