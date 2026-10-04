from urllib.parse import parse_qs

def parse_query(text):
    return parse_qs(text)
