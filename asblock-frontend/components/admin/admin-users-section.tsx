'use client'

import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import Link from 'next/link'
import { Loader2, RefreshCw } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
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
import { QueryEmptyState } from '@/components/shared/query-empty-state'
import { routes } from '@/lib/routes'
import {
  ADMIN_USERS_PAGE_SIZE,
  adminUserKeys,
  assignAdminUserRole,
  fetchAdminUsersPage,
} from '@/lib/admin/admin-users-query'

const ASSIGNABLE_ROLES = ['User', 'Moderator'] as const

interface RoleSelection {
  userId: string
  role: (typeof ASSIGNABLE_ROLES)[number]
  expectedRoleRevision: number
}

export function AdminUsersSection() {
  const queryClient = useQueryClient()
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [selection, setSelection] = useState<RoleSelection | null>(null)

  const usersQuery = useQuery({
    queryKey: adminUserKeys.list({ search, page }),
    queryFn: ({ signal }) => fetchAdminUsersPage({ search, page, signal }),
    placeholderData: (previous) => previous,
  })

  const items = usersQuery.data?.items ?? []
  const totalCount = usersQuery.data?.totalCount ?? 0
  const totalPages = Math.max(1, Math.ceil(totalCount / ADMIN_USERS_PAGE_SIZE))
  const displayPage = Math.min(page, totalPages)

  const roleMutation = useMutation({
    mutationFn: assignAdminUserRole,
    onSuccess: (outcome) => {
      setSelection(null)
      if (!outcome.ok) {
        if (outcome.roleRevisionStale) {
          toast.error('This user was changed by someone else. The list has been refreshed.')
          void queryClient.invalidateQueries({ queryKey: adminUserKeys.all })
        } else {
          toast.error(outcome.message)
        }
        return
      }
      toast.success(`Role updated to ${outcome.result.role}.`)
      void queryClient.invalidateQueries({ queryKey: adminUserKeys.all })
    },
    onError: (error: Error) => {
      setSelection(null)
      toast.error(error.message)
    },
  })

  function confirmRoleChange() {
    if (!selection) return
    roleMutation.mutate({
      userId: selection.userId,
      role: selection.role,
      expectedRoleRevision: selection.expectedRoleRevision,
    })
  }

  const searchPending = usersQuery.isPlaceholderData
  const loading = usersQuery.isPending

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-sm font-semibold text-foreground">Users</h2>
        <p className="mt-1 text-xs text-muted-foreground">
          Assign the User or Moderator role. Admin accounts cannot be granted or modified here.
        </p>
      </div>

      <form
        className="flex gap-2 max-w-md"
        onSubmit={(event) => {
          event.preventDefault()
          setPage(1)
          setSearch(searchInput.trim())
        }}
      >
        <Input
          aria-label="Search users"
          placeholder="Search username or email"
          className="bg-input border-border"
          value={searchInput}
          onChange={(event) => setSearchInput(event.target.value)}
        />
        <Button type="submit" variant="outline" className="border-border shrink-0">
          Search
        </Button>
      </form>

      {loading ? (
        <div className="flex items-center gap-2 text-sm text-muted-foreground py-6">
          <Loader2 className="size-4 animate-spin" aria-hidden />
          Loading users…
        </div>
      ) : usersQuery.isError ? (
        <div className="space-y-3 py-6">
          <p className="text-sm text-destructive" role="alert">
            {usersQuery.error instanceof Error ? usersQuery.error.message : 'Could not load users.'}
          </p>
          <Button
            type="button"
            variant="outline"
            className="border-border"
            onClick={() => usersQuery.refetch()}
          >
            <RefreshCw className="mr-1.5 size-3.5" aria-hidden />
            Retry
          </Button>
        </div>
      ) : items.length === 0 ? (
        <QueryEmptyState title="No users found" description="Try a different search term." />
      ) : (
        <div className="rounded-lg border border-border overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Username</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Role</TableHead>
                <TableHead className="w-56">Change role</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((user) => {
                const isProtectedAdmin = user.role === 'Admin'
                const selected = selection?.userId === user.id ? selection.role : null
                return (
                  <TableRow key={user.id}>
                    <TableCell className="font-medium">
                      <Link href={routes.userProfile(user.username)} className="hover:underline">
                        {user.username}
                      </Link>
                    </TableCell>
                    <TableCell className="text-muted-foreground">{user.email}</TableCell>
                    <TableCell>
                      <Badge
                        variant="outline"
                        className={
                          isProtectedAdmin
                            ? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-200'
                            : undefined
                        }
                      >
                        {user.role}
                      </Badge>
                    </TableCell>
                    <TableCell>
                      {isProtectedAdmin ? (
                        <span className="text-xs text-muted-foreground">Protected</span>
                      ) : (
                        <div className="flex items-center gap-2">
                          <select
                            aria-label={`Change role for ${user.username}`}
                            className="border-input bg-input h-8 rounded-md border px-2 text-xs outline-none focus-visible:border-ring"
                            value={selected ?? user.role}
                            onChange={(event) => {
                              const role = event.target.value
                              if (role === user.role) {
                                setSelection((current) =>
                                  current?.userId === user.id ? null : current,
                                )
                                return
                              }
                              if ((ASSIGNABLE_ROLES as readonly string[]).includes(role)) {
                                setSelection({
                                  userId: user.id,
                                  role: role as (typeof ASSIGNABLE_ROLES)[number],
                                  expectedRoleRevision: user.roleRevision,
                                })
                              }
                            }}
                          >
                            {ASSIGNABLE_ROLES.map((role) => (
                              <option key={role} value={role}>
                                {role}
                              </option>
                            ))}
                          </select>
                          {selection?.userId === user.id ? (
                            <Button
                              type="button"
                              size="sm"
                              className="bg-primary text-primary-foreground hover:bg-[#6D28D9]"
                              disabled={roleMutation.isPending}
                              onClick={confirmRoleChange}
                            >
                              {roleMutation.isPending ? (
                                <Loader2 className="size-3.5 animate-spin" aria-hidden />
                              ) : (
                                'Apply'
                              )}
                            </Button>
                          ) : null}
                        </div>
                      )}
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        </div>
      )}

      {totalPages > 1 ? (
        <div className="flex items-center gap-3 text-xs text-muted-foreground">
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="border-border"
            disabled={page <= 1 || searchPending}
            onClick={() => setPage((current) => Math.max(1, current - 1))}
          >
            Previous
          </Button>
          <span>
            Page {displayPage} of {totalPages}
          </span>
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="border-border"
            disabled={page >= totalPages || searchPending}
            onClick={() => setPage((current) => Math.min(totalPages, current + 1))}
          >
            Next
          </Button>
        </div>
      ) : null}
    </div>
  )
}
