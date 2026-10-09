'use client'

import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { AdminAuditLogsView } from '@/components/admin/admin-audit-logs-view'
import { AdminCategoriesSection } from '@/components/admin/admin-categories-section'
import { AdminReviewsSection } from '@/components/admin/admin-reviews-section'
import { AdminTagsSection } from '@/components/admin/admin-tags-section'
import { AdminUsersSection } from '@/components/admin/admin-users-section'

export function AdminPanelClient() {
  return (
    <Tabs defaultValue="categories" className="w-full">
      <TabsList className="grid w-full max-w-3xl grid-cols-5 bg-secondary/40 border border-border">
        <TabsTrigger value="categories" className="text-xs">
          Categories
        </TabsTrigger>
        <TabsTrigger value="tags" className="text-xs">
          Tags
        </TabsTrigger>
        <TabsTrigger value="users" className="text-xs">
          Users
        </TabsTrigger>
        <TabsTrigger value="reviews" className="text-xs">
          Reviews
        </TabsTrigger>
        <TabsTrigger value="audit-logs" className="text-xs">
          Audit logs
        </TabsTrigger>
      </TabsList>
      <TabsContent value="categories" className="mt-6">
        <AdminCategoriesSection />
      </TabsContent>
      <TabsContent value="tags" className="mt-6">
        <AdminTagsSection />
      </TabsContent>
      <TabsContent value="users" className="mt-6">
        <AdminUsersSection />
      </TabsContent>
      <TabsContent value="reviews" className="mt-6">
        <AdminReviewsSection />
      </TabsContent>
      <TabsContent value="audit-logs" className="mt-6">
        <AdminAuditLogsView />
      </TabsContent>
    </Tabs>
  )
}
