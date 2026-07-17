'use client'

import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { AdminAuditLogsView } from '@/components/admin/admin-audit-logs-view'
import { AdminCategoriesSection } from '@/components/admin/admin-categories-section'
import { AdminReviewsSection } from '@/components/admin/admin-reviews-section'
import { AdminTagsSection } from '@/components/admin/admin-tags-section'

export function AdminPanelClient() {
  return (
    <Tabs defaultValue="categories" className="w-full">
      <TabsList className="grid w-full max-w-2xl grid-cols-4 bg-secondary/40 border border-border">
        <TabsTrigger value="categories" className="text-xs">
          Categories
        </TabsTrigger>
        <TabsTrigger value="tags" className="text-xs">
          Tags
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
      <TabsContent value="reviews" className="mt-6">
        <AdminReviewsSection />
      </TabsContent>
      <TabsContent value="audit-logs" className="mt-6">
        <AdminAuditLogsView />
      </TabsContent>
    </Tabs>
  )
}
