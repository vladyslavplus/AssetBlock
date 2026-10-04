def match_status(code: int) -> str:
    match code:
        case 200:
            return 'ok'
        case _:
            return 'other'
