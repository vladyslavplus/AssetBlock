import endpoints from './moderation-endpoints.json'

export const moderationBackendEndpoints = endpoints

export function moderationBackendPath(
  kind: keyof typeof endpoints,
  id?: string,
  evidenceKind?: string,
) {
  let path = endpoints[kind].path
  if (id !== undefined) path = path.replace('{id}', encodeURIComponent(id))
  if (evidenceKind !== undefined) path = path.replace('{kind}', encodeURIComponent(evidenceKind))
  return path
}
