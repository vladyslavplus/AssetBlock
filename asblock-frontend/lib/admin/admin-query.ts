import { apiFetch } from '@/lib/http/api-client'
import type { CategoryListItemApi, PagedResultDto, TagDtoApi } from '@/lib/catalog/assets-api'

export const adminKeys = {
  all: ['admin'] as const,
  categories: () => [...adminKeys.all, 'categories'] as const,
  tags: () => [...adminKeys.all, 'tags'] as const,
}

/** Admin tables: server-side paging (matches backend clamp max 100). */
export const ADMIN_LIST_PAGE_SIZE = 20

export async function fetchCategoriesAdminPage(params: {
  page: number
  pageSize?: number
  search?: string
}): Promise<PagedResultDto<CategoryListItemApi>> {
  const pageSize = params.pageSize ?? ADMIN_LIST_PAGE_SIZE
  const qs = new URLSearchParams({
    page: String(Math.max(1, params.page)),
    pageSize: String(pageSize),
    sortBy: 'Name',
    sortDirection: 'ASC',
  })
  const q = params.search?.trim()
  if (q) {
    qs.set('search', q)
  }
  return apiFetch<PagedResultDto<CategoryListItemApi>>({
    path: `api/categories?${qs.toString()}`,
    method: 'GET',
  })
}

export async function fetchTagsAdminPage(params: {
  page: number
  pageSize?: number
  search?: string
}): Promise<PagedResultDto<TagDtoApi>> {
  const pageSize = params.pageSize ?? ADMIN_LIST_PAGE_SIZE
  const qs = new URLSearchParams({
    page: String(Math.max(1, params.page)),
    pageSize: String(pageSize),
    sortBy: 'name',
    sortDirection: 'ASC',
  })
  const q = params.search?.trim()
  if (q) {
    qs.set('search', q)
  }
  return apiFetch<PagedResultDto<TagDtoApi>>({
    path: `api/tags?${qs.toString()}`,
    method: 'GET',
  })
}
