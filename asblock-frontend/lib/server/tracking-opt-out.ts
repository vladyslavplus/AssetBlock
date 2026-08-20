/** True when browser DNT/GPC headers or an explicit client flag opt out of analytics tracking. */
export function isTrackingOptedOut(request: Request, doNotTrack?: boolean): boolean {
  if (doNotTrack === true) {
    return true
  }
  if (request.headers.get('DNT') === '1') {
    return true
  }
  if (request.headers.get('Sec-GPC') === '1') {
    return true
  }
  return false
}
