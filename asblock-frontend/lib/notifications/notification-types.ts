export interface NotificationListItem {
  id: string
  kind: string
  metadataJson: string
  createdAt: string
  readAt: string | null
}

export interface PagedNotificationsDto {
  items: NotificationListItem[]
  totalCount: number
  page: number
  pageSize: number
  totalPages?: number
}
