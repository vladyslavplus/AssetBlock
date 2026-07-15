'use client'

import { AdminCategoriesView } from '@/components/admin/admin-categories-view'
import { useAdminCategories } from '@/lib/admin/use-admin-categories'

export function AdminCategoriesSection() {
  const controller = useAdminCategories()
  return <AdminCategoriesView controller={controller} />
}
