'use client'

import { ChevronLeft, ChevronRight, Pencil, Plus, Search, Trash2 } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { AdminCategoryDialogs } from '@/components/admin/admin-category-dialogs'
import type { AdminCategoriesController } from '@/lib/admin/use-admin-categories'

interface AdminCategoriesViewProps {
  controller: AdminCategoriesController
}

export function AdminCategoriesView({ controller }: AdminCategoriesViewProps) {
  const {
    page,
    debouncedSearch,
    searchInput,
    setSearchInput,
    setPage,
    setCreateOpen,
    setDeleting,
    listQuery,
    rows,
    totalCount,
    totalPages,
    rangeStart,
    rangeEnd,
    openEdit,
  } = controller

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center sm:justify-between">
        <p className="text-sm text-muted-foreground">
          Create, edit, or remove categories. Search runs on the server (name, slug, description).
        </p>
        <Button
          type="button"
          size="sm"
          className="bg-primary text-primary-foreground hover:bg-[#6D28D9] shrink-0 self-start sm:self-auto"
          onClick={() => setCreateOpen(true)}
        >
          <Plus className="size-3.5 mr-1.5" aria-hidden />
          New category
        </Button>
      </div>

      <div className="relative max-w-md">
        <Search
          className="absolute left-2.5 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground pointer-events-none"
          aria-hidden
        />
        <Input
          type="search"
          value={searchInput}
          onChange={(event) => setSearchInput(event.target.value)}
          placeholder="Search name, slug, or description…"
          className="h-9 pl-8 text-xs bg-input border-border"
          aria-label="Search categories"
        />
      </div>

      {listQuery.isPending && !listQuery.data ? (
        <p className="text-sm text-muted-foreground py-6">Loading categories…</p>
      ) : listQuery.isError ? (
        <p className="text-sm text-destructive py-4">Could not load categories.</p>
      ) : (
        <>
          <div className="rounded-lg border border-border overflow-x-auto">
            <Table className="table-fixed min-w-[44rem]">
              <TableHeader>
                <TableRow>
                  <TableHead className="text-xs w-[20%] min-w-[8rem]">Name</TableHead>
                  <TableHead className="text-xs w-[20%] min-w-[8rem]">Slug</TableHead>
                  <TableHead className="text-xs w-[50%] min-w-0">Description</TableHead>
                  <TableHead className="text-xs w-[10%] min-w-[5.5rem] text-right">
                    Actions
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.length === 0 ? (
                  <TableRow>
                    <TableCell
                      colSpan={4}
                      className="text-xs text-muted-foreground text-center py-8"
                    >
                      {debouncedSearch
                        ? 'No categories match this search.'
                        : 'No categories in the catalog.'}
                    </TableCell>
                  </TableRow>
                ) : null}
                {rows.map((row) => (
                  <TableRow key={row.id}>
                    <TableCell className="text-xs font-medium min-w-0 max-w-0 truncate">
                      {row.name}
                    </TableCell>
                    <TableCell className="text-xs font-mono text-muted-foreground min-w-0 max-w-0 truncate">
                      {row.slug}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground min-w-0 max-w-0 truncate">
                      {row.description ?? '—'}
                    </TableCell>
                    <TableCell className="text-right w-[10%] min-w-[5.5rem] whitespace-nowrap">
                      <div className="flex justify-end gap-1">
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="h-8 w-8"
                          aria-label={`Edit ${row.name}`}
                          onClick={() => openEdit(row)}
                        >
                          <Pencil className="size-3.5" />
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="h-8 w-8 text-destructive hover:text-destructive"
                          aria-label={`Delete ${row.name}`}
                          onClick={() => setDeleting(row)}
                        >
                          <Trash2 className="size-3.5" />
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          {totalPages > 0 ? (
            <div className="flex flex-col sm:flex-row items-center justify-between gap-3 pt-2">
              <p className="text-xs text-muted-foreground tabular-nums">
                {rangeStart}–{rangeEnd} of {totalCount} · Page {page} of {totalPages}
              </p>
              <div className="flex items-center gap-2">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="h-8 px-2"
                  disabled={page <= 1 || listQuery.isFetching}
                  onClick={() => setPage(Math.max(1, page - 1))}
                  aria-label="Previous page"
                >
                  <ChevronLeft className="size-4" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="h-8 px-2"
                  disabled={page >= totalPages || listQuery.isFetching}
                  onClick={() => setPage(page + 1)}
                  aria-label="Next page"
                >
                  <ChevronRight className="size-4" />
                </Button>
              </div>
            </div>
          ) : null}
        </>
      )}

      <AdminCategoryDialogs controller={controller} />
    </div>
  )
}
