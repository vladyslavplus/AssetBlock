'use client'

import { zodResolver } from '@hookform/resolvers/zod'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useReducer, useState } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'

import { adminDelete, adminPostJson, adminPutJson } from '@/lib/admin/admin-bff'
import { ADMIN_LIST_PAGE_SIZE, adminKeys, fetchCategoriesAdminPage } from '@/lib/admin/admin-query'
import {
  adminCategoryCreateSchema,
  adminCategoryUpdateSchema,
  type AdminCategoryCreateInput,
  type AdminCategoryUpdateInput,
} from '@/lib/admin/admin-schemas'
import type { CategoryListItemApi } from '@/lib/catalog/assets-api'
import { catalogKeys } from '@/lib/catalog/catalog-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'

const SEARCH_DEBOUNCE_MS = 320

interface ListState {
  page: number
  debouncedSearch: string
}

type ListAction =
  | { type: 'apply_debounced_search'; payload: string }
  | { type: 'set_page'; payload: number }

function listReducer(state: ListState, action: ListAction): ListState {
  switch (action.type) {
    case 'apply_debounced_search':
      return state.debouncedSearch === action.payload
        ? state
        : { debouncedSearch: action.payload, page: 1 }
    case 'set_page':
      return state.page === action.payload ? state : { ...state, page: action.payload }
  }
}

export function useAdminCategories() {
  const queryClient = useQueryClient()
  const [{ page, debouncedSearch }, dispatch] = useReducer(listReducer, {
    page: 1,
    debouncedSearch: '',
  })
  const [searchInput, setSearchInput] = useState('')
  const [createOpen, setCreateOpen] = useState(false)
  const [editing, setEditing] = useState<CategoryListItemApi | null>(null)
  const [deleting, setDeleting] = useState<CategoryListItemApi | null>(null)

  useEffect(() => {
    const timer = window.setTimeout(() => {
      dispatch({ type: 'apply_debounced_search', payload: searchInput.trim() })
    }, SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [searchInput])

  const listQuery = useQuery({
    queryKey: [...adminKeys.categories(), page, debouncedSearch] as const,
    queryFn: () =>
      fetchCategoriesAdminPage({
        page,
        pageSize: ADMIN_LIST_PAGE_SIZE,
        search: debouncedSearch || undefined,
      }),
    placeholderData: keepPreviousData,
  })

  const createForm = useForm<AdminCategoryCreateInput>({
    resolver: zodResolver(adminCategoryCreateSchema),
    defaultValues: { name: '', description: '', slug: '' },
  })
  const updateForm = useForm<AdminCategoryUpdateInput>({
    resolver: zodResolver(adminCategoryUpdateSchema),
    defaultValues: { name: '', description: '', slug: '' },
  })

  const invalidateLists = () => {
    invalidateQueriesInBackground(queryClient, { queryKey: adminKeys.categories() })
    invalidateQueriesInBackground(queryClient, { queryKey: catalogKeys.all })
  }

  const createMutation = useMutation({
    mutationFn: (body: AdminCategoryCreateInput) =>
      adminPostJson('/api/admin/categories', {
        name: body.name.trim(),
        description: body.description?.trim() || null,
        slug: body.slug.trim(),
      }),
    onSuccess: () => {
      toast.success('Category created.')
      invalidateLists()
      setCreateOpen(false)
      createForm.reset({ name: '', description: '', slug: '' })
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Record<string, unknown> }) =>
      adminPutJson(`/api/admin/categories/${encodeURIComponent(id)}`, body),
    onSuccess: () => {
      toast.success('Category updated.')
      invalidateLists()
      setEditing(null)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => adminDelete(`/api/admin/categories/${encodeURIComponent(id)}`),
    onSuccess: () => {
      toast.success('Category deleted.')
      invalidateLists()
      setDeleting(null)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const openEdit = (row: CategoryListItemApi) => {
    setEditing(row)
    updateForm.reset({
      name: row.name,
      description: row.description ?? '',
      slug: row.slug,
    })
  }

  const submitUpdate = (values: AdminCategoryUpdateInput) => {
    if (!editing) return

    const body: Record<string, unknown> = {}
    if (values.name.trim() !== editing.name) body.name = values.name.trim()

    const description = values.description?.trim() ?? ''
    if (description !== (editing.description ?? '')) {
      body.description = description || null
    }
    if (values.slug.trim() !== editing.slug) body.slug = values.slug.trim()

    if (Object.keys(body).length === 0) {
      toast.message('No changes to save.')
      return
    }
    updateMutation.mutate({ id: editing.id, body })
  }

  const pageData = listQuery.data
  const totalCount = pageData?.totalCount ?? 0
  const pageSize = pageData?.pageSize ?? ADMIN_LIST_PAGE_SIZE
  const totalPages = totalCount === 0 ? 0 : Math.ceil(totalCount / pageSize)

  return {
    page,
    debouncedSearch,
    searchInput,
    setSearchInput,
    setPage: (nextPage: number) => dispatch({ type: 'set_page', payload: nextPage }),
    createOpen,
    setCreateOpen,
    editing,
    setEditing,
    deleting,
    setDeleting,
    listQuery,
    rows: pageData?.items ?? [],
    totalCount,
    totalPages,
    rangeStart: totalCount === 0 ? 0 : (page - 1) * pageSize + 1,
    rangeEnd: totalCount === 0 ? 0 : Math.min(page * pageSize, totalCount),
    createForm,
    updateForm,
    createMutation,
    updateMutation,
    deleteMutation,
    openEdit,
    submitUpdate,
  }
}

export type AdminCategoriesController = ReturnType<typeof useAdminCategories>
