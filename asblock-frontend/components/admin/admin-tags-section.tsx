"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, Pencil, Plus, Search, Trash2 } from "lucide-react";
import { useEffect, useReducer, useState } from "react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";
import type { z } from "zod";
import { adminDelete, adminPostJson, adminPutJson } from "@/lib/admin/admin-bff";
import { ADMIN_LIST_PAGE_SIZE, adminKeys, fetchTagsAdminPage } from "@/lib/admin/admin-query";
import { adminTagCreateSchema, adminTagUpdateSchema } from "@/lib/admin/admin-schemas";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { TagDtoApi } from "@/lib/catalog/assets-api";
import { catalogKeys } from "@/lib/catalog/catalog-query";

type CreateForm = z.infer<typeof adminTagCreateSchema>;
type UpdateForm = z.infer<typeof adminTagUpdateSchema>;

const SEARCH_DEBOUNCE_MS = 320;

interface AdminTagsListState {
  page: number;
  debouncedSearch: string;
}
type AdminTagsListAction =
  | { type: "apply_debounced_search"; payload: string }
  | { type: "set_page"; payload: number };

function adminTagsListReducer(
  state: AdminTagsListState,
  action: AdminTagsListAction,
): AdminTagsListState {
  switch (action.type) {
    case "apply_debounced_search": {
      const next = action.payload;
      if (state.debouncedSearch === next) {
        return state;
      }
      return { debouncedSearch: next, page: 1 };
    }
    case "set_page": {
      if (state.page === action.payload) {
        return state;
      }
      return { ...state, page: action.payload };
    }
    default:
      return state;
  }
}

const ADMIN_TAGS_LIST_INITIAL: AdminTagsListState = {
  page: 1,
  debouncedSearch: "",
};

export function AdminTagsSection() {
  const queryClient = useQueryClient();
  const [{ page, debouncedSearch }, dispatchList] = useReducer(
    adminTagsListReducer,
    ADMIN_TAGS_LIST_INITIAL,
  );
  const [searchInput, setSearchInput] = useState("");
  const [createOpen, setCreateOpen] = useState(false);
  const [editing, setEditing] = useState<TagDtoApi | null>(null);
  const [deleting, setDeleting] = useState<TagDtoApi | null>(null);

  useEffect(() => {
    const t = window.setTimeout(() => {
      dispatchList({ type: "apply_debounced_search", payload: searchInput.trim() });
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(t);
  }, [searchInput]);

  const listQuery = useQuery({
    queryKey: [...adminKeys.tags(), page, debouncedSearch] as const,
    queryFn: () =>
      fetchTagsAdminPage({
        page,
        pageSize: ADMIN_LIST_PAGE_SIZE,
        search: debouncedSearch || undefined,
      }),
    placeholderData: keepPreviousData,
  });

  const createForm = useForm<CreateForm>({
    resolver: zodResolver(adminTagCreateSchema),
    defaultValues: { name: "" },
  });

  const updateForm = useForm<UpdateForm>({
    resolver: zodResolver(adminTagUpdateSchema),
    defaultValues: { name: "" },
  });

  const createMutation = useMutation({
    mutationFn: (body: CreateForm) => adminPostJson("/api/admin/tags", { name: body.name.trim() }),
    onSuccess: () => {
      toast.success("Tag created.");
      void queryClient.invalidateQueries({ queryKey: adminKeys.tags() });
      void queryClient.invalidateQueries({ queryKey: catalogKeys.all });
      setCreateOpen(false);
      createForm.reset({ name: "" });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      adminPutJson(`/api/admin/tags/${encodeURIComponent(id)}`, { name }),
    onSuccess: () => {
      toast.success("Tag updated.");
      void queryClient.invalidateQueries({ queryKey: adminKeys.tags() });
      void queryClient.invalidateQueries({ queryKey: catalogKeys.all });
      setEditing(null);
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => adminDelete(`/api/admin/tags/${encodeURIComponent(id)}`),
    onSuccess: () => {
      toast.success("Tag deleted.");
      void queryClient.invalidateQueries({ queryKey: adminKeys.tags() });
      void queryClient.invalidateQueries({ queryKey: catalogKeys.all });
      setDeleting(null);
    },
    onError: (e: Error) => toast.error(e.message),
  });

  function openEdit(row: TagDtoApi) {
    setEditing(row);
    updateForm.reset({ name: row.name });
  }

  const pageData = listQuery.data;
  const rows = pageData?.items ?? [];
  const totalCount = pageData?.totalCount ?? 0;
  const pageSize = pageData?.pageSize ?? ADMIN_LIST_PAGE_SIZE;
  const totalPages = totalCount === 0 ? 0 : Math.ceil(totalCount / pageSize);
  const rangeStart = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const rangeEnd = totalCount === 0 ? 0 : Math.min(page * pageSize, totalCount);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center sm:justify-between">
        <p className="text-sm text-muted-foreground">
          Manage tags. Search runs on the server (tag name).
        </p>
        <Button
          type="button"
          size="sm"
          className="bg-primary text-primary-foreground hover:bg-[#6D28D9] shrink-0 self-start sm:self-auto"
          onClick={() => setCreateOpen(true)}
        >
          <Plus className="size-3.5 mr-1.5" aria-hidden />
          New tag
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
          onChange={(e) => setSearchInput(e.target.value)}
          placeholder="Search tag name…"
          className="h-9 pl-8 text-xs bg-input border-border"
          aria-label="Search tags"
        />
      </div>

      {listQuery.isPending && !pageData ? (
        <p className="text-sm text-muted-foreground py-6">Loading tags…</p>
      ) : listQuery.isError ? (
        <p className="text-sm text-destructive py-4">Could not load tags.</p>
      ) : (
        <>
        <div className="rounded-lg border border-border overflow-x-auto">
          <Table className="table-fixed min-w-[40rem]">
            <TableHeader>
              <TableRow>
                <TableHead className="text-xs w-[28%] min-w-[7rem]">Name</TableHead>
                <TableHead className="text-xs font-mono w-[62%] min-w-0">ID</TableHead>
                <TableHead className="text-xs w-[10%] min-w-[5.5rem] text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={3} className="text-xs text-muted-foreground text-center py-8">
                    {debouncedSearch ? "No tags match this search." : "No tags in the catalog."}
                  </TableCell>
                </TableRow>
              ) : null}
              {rows.map((row) => (
                <TableRow key={row.id}>
                  <TableCell className="text-xs font-medium min-w-0 max-w-0 truncate">
                    {row.name}
                  </TableCell>
                  <TableCell className="text-[10px] font-mono text-muted-foreground min-w-0 max-w-0 truncate">
                    {row.id}
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
                onClick={() => dispatchList({ type: "set_page", payload: Math.max(1, page - 1) })}
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
                onClick={() => dispatchList({ type: "set_page", payload: page + 1 })}
                aria-label="Next page"
              >
                <ChevronRight className="size-4" />
              </Button>
            </div>
          </div>
        ) : null}
        </>
      )}

      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent className="border-border bg-card">
          <DialogHeader>
            <DialogTitle>New tag</DialogTitle>
            <DialogDescription>Tag name must be unique.</DialogDescription>
          </DialogHeader>
          <form
            className="space-y-3"
            onSubmit={createForm.handleSubmit((v) => createMutation.mutate(v))}
          >
            <div className="space-y-1.5">
              <Label htmlFor="tag-create-name">Name</Label>
              <Input id="tag-create-name" {...createForm.register("name")} />
              {createForm.formState.errors.name && (
                <p className="text-xs text-destructive">{createForm.formState.errors.name.message}</p>
              )}
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" disabled={createMutation.isPending}>
                {createMutation.isPending ? "Saving…" : "Create"}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="border-border bg-card">
          <DialogHeader>
            <DialogTitle>Edit tag</DialogTitle>
          </DialogHeader>
          <form
            className="space-y-3"
            onSubmit={updateForm.handleSubmit((v) =>
              editing ? updateMutation.mutate({ id: editing.id, name: v.name.trim() }) : undefined,
            )}
          >
            <div className="space-y-1.5">
              <Label htmlFor="tag-edit-name">Name</Label>
              <Input id="tag-edit-name" {...updateForm.register("name")} />
              {updateForm.formState.errors.name && (
                <p className="text-xs text-destructive">{updateForm.formState.errors.name.message}</p>
              )}
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setEditing(null)}>
                Cancel
              </Button>
              <Button type="submit" disabled={updateMutation.isPending}>
                {updateMutation.isPending ? "Saving…" : "Save"}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={deleting !== null} onOpenChange={(o) => !o && setDeleting(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete tag?</AlertDialogTitle>
            <AlertDialogDescription>
              {deleting ? `“${deleting.name}” will be removed from the global tag list.` : null}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
              onClick={() => deleting && deleteMutation.mutate(deleting.id)}
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
