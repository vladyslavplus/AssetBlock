'use client'

import { AdminAuditLogsSection } from '@/components/admin/admin-audit-logs-section'
import { useAdminAuditLogs } from '@/lib/admin/use-admin-audit-logs'

export function AdminAuditLogsView() {
  const controller = useAdminAuditLogs()
  return <AdminAuditLogsSection controller={controller} />
}
