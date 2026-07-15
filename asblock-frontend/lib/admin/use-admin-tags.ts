'use client'

import { zodResolver } from '@hookform/resolvers/zod'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useReducer, useState } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'

import { adminDelete, adminPostJson, adminPutJson } from '@/lib/admin/admin-bff'
import { ADMIN_LIST_PAGE_SIZE, adminKeys, fetchTagsAdminPage } from '@/lib/admin/admin-query'
import {
  adminTagCreateSchema,
  adminTagUpdateSchema,
  type AdminTagCreateInput,
  type AdminTagUpdateInput,
} from '@/lib/admin/admin-schemas'
import type { TagDtoApi } from '@/lib/catalog/assets-api'
import { catalogKeys } from '@/lib/catalog/catalog-query'

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

export function useAdminTags() {
  const queryClient = useQueryClient()
  const [{ page, debouncedSearch }, dispatch] = useReducer(listReducer, {
    page: 1,
    debouncedSearch: '',
  })
  const [searchInput, setSearchInput] = useState('')
  const [createOpen, setCreateOpen] = useState(false)
  const [editing, setEditing] = useState<TagDtoApi | null>(null)
  const [deleting, setDeleting] = useState<TagDtoApi | null>(null)

  useEffect(() => {
    const timer = window.setTimeout(() => {
      dispatch({ type: 'apply_debounced_search', payload: searchInput.trim() })
    }, SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [searchInput])

  const listQuery = useQuery({
    queryKey: [...adminKeys.tags(), page, debouncedSearch] as const,
    queryFn: () =>
      fetchTagsAdminPage({
        page,
        pageSize: ADMIN_LIST_PAGE_SIZE,
        search: debouncedSearch || undefined,
      }),
    placeholderData: keepPreviousData,
  })

  const createForm = useForm<AdminTagCreateInput>({
    resolver: zodResolver(adminTagCreateSchema),
    defaultValues: { name: '' },
  })
  const updateForm = useForm<AdminTagUpdateInput>({
    resolver: zodResolver(adminTagUpdateSchema),
    defaultValues: { name: '' },
  })

  const invalidateLists = () => {
    void queryClient.invalidateQueries({ queryKey: adminKeys.tags() })
    void queryClient.invalidateQueries({ queryKey: catalogKeys.all })
  }

  const createMutation = useMutation({
    mutationFn: (body: AdminTagCreateInput) =>
      adminPostJson('/api/admin/tags', { name: body.name.trim() }),
    onSuccess: () => {
      toast.success('Tag created.')
      invalidateLists()
      setCreateOpen(false)
      createForm.reset({ name: '' })
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      adminPutJson(`/api/admin/tags/${encodeURIComponent(id)}`, { name }),
    onSuccess: () => {
      toast.success('Tag updated.')
      invalidateLists()
      setEditing(null)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => adminDelete(`/api/admin/tags/${encodeURIComponent(id)}`),
    onSuccess: () => {
      toast.success('Tag deleted.')
      invalidateLists()
      setDeleting(null)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const openEdit = (row: TagDtoApi) => {
    setEditing(row)
    updateForm.reset({ name: row.name })
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
  }
}

export type AdminTagsController = ReturnType<typeof useAdminTags>
