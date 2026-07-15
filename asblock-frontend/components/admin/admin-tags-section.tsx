'use client'

import { AdminTagsView } from '@/components/admin/admin-tags-view'
import { useAdminTags } from '@/lib/admin/use-admin-tags'

export function AdminTagsSection() {
  const controller = useAdminTags()
  return <AdminTagsView controller={controller} />
}
