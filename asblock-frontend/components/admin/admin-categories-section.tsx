"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, Pencil, Plus, Search, Trash2 } from "lucide-react";
import { useEffect, useReducer, useState } from "react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";
import { adminDelete, adminPostJson, adminPutJson } from "@/lib/admin/admin-bff";
import { ADMIN_LIST_PAGE_SIZE, adminKeys, fetchCategoriesAdminPage } from "@/lib/admin/admin-query";
import { adminCategoryCreateSchema, adminCategoryUpdateSchema } from "@/lib/admin/admin-schemas";
import type { z } from "zod";
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
import { Textarea } from "@/components/ui/textarea";
import type { CategoryListItemApi } from "@/lib/catalog/assets-api";
import { catalogKeys } from "@/lib/catalog/catalog-query";

type CreateForm = z.infer<typeof adminCategoryCreateSchema>;
type UpdateForm = z.infer<typeof adminCategoryUpdateSchema>;

function slugifyHint(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-|-$/g, "");
}

const SEARCH_DEBOUNCE_MS = 320;

interface AdminCategoriesListState {
  page: number;
  debouncedSearch: string;
}
type AdminCategoriesListAction =
  | { type: "apply_debounced_search"; payload: string }
  | { type: "set_page"; payload: number };

function adminCategoriesListReducer(
  state: AdminCategoriesListState,
  action: AdminCategoriesListAction,
): AdminCategoriesListState {
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

const ADMIN_CATEGORIES_LIST_INITIAL: AdminCategoriesListState = {
  page: 1,
  debouncedSearch: "",
};

export function AdminCategoriesSection() {
  const queryClient = useQueryClient();
  const [{ page, debouncedSearch }, dispatchList] = useReducer(
    adminCategoriesListReducer,
    ADMIN_CATEGORIES_LIST_INITIAL,
  );
  const [searchInput, setSearchInput] = useState("");
  const [createOpen, setCreateOpen] = useState(false);
  const [editing, setEditing] = useState<CategoryListItemApi | null>(null);
  const [deleting, setDeleting] = useState<CategoryListItemApi | null>(null);

  useEffect(() => {
    const t = window.setTimeout(() => {
      dispatchList({ type: "apply_debounced_search", payload: searchInput.trim() });
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(t);
  }, [searchInput]);

  const listQuery = useQuery({
    queryKey: [...adminKeys.categories(), page, debouncedSearch] as const,
    queryFn: () =>
      fetchCategoriesAdminPage({
        page,
        pageSize: ADMIN_LIST_PAGE_SIZE,
        search: debouncedSearch || undefined,
      }),
    placeholderData: keepPreviousData,
  });

  const createForm = useForm<CreateForm>({
    resolver: zodResolver(adminCategoryCreateSchema),
    defaultValues: { name: "", description: "", slug: "" },
  });

  const updateForm = useForm<UpdateForm>({
    resolver: zodResolver(adminCategoryUpdateSchema),
    defaultValues: { name: "", description: "", slug: "" },
  });

  const createMutation = useMutation({
    mutationFn: (body: CreateForm) =>
      adminPostJson("/api/admin/categories", {
        name: body.name.trim(),
        description: body.description?.trim() || null,
        slug: body.slug.trim(),
      }),
    onSuccess: () => {
      toast.success("Category created.");
      void queryClient.invalidateQueries({ queryKey: adminKeys.categories() });
      void queryClient.invalidateQueries({ queryKey: catalogKeys.all });
      setCreateOpen(false);
      createForm.reset({ name: "", description: "", slug: "" });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Record<string, unknown> }) =>
      adminPutJson(`/api/admin/categories/${encodeURIComponent(id)}`, body),
    onSuccess: () => {
      toast.success("Category updated.");
      void queryClient.invalidateQueries({ queryKey: adminKeys.categories() });
      void queryClient.invalidateQueries({ queryKey: catalogKeys.all });
      setEditing(null);
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => adminDelete(`/api/admin/categories/${encodeURIComponent(id)}`),
    onSuccess: () => {
      toast.success("Category deleted.");
      void queryClient.invalidateQueries({ queryKey: adminKeys.categories() });
      void queryClient.invalidateQueries({ queryKey: catalogKeys.all });
      setDeleting(null);
    },
    onError: (e: Error) => toast.error(e.message),
  });

  function openEdit(row: CategoryListItemApi) {
    setEditing(row);
    updateForm.reset({
      name: row.name,
      description: row.description ?? "",
      slug: row.slug,
    });
  }

  function submitUpdate(values: UpdateForm) {
    if (!editing) {
      return;
    }
    const body: Record<string, unknown> = {};
    if (values.name != null && values.name.trim() !== editing.name) {
      body.name = values.name.trim();
    }
    const desc = values.description?.trim() ?? "";
    const prevDesc = editing.description ?? "";
    if (desc !== prevDesc) {
      body.description = desc.length > 0 ? desc : null;
    }
    if (values.slug != null && values.slug.trim() !== editing.slug) {
      body.slug = values.slug.trim();
    }
    if (Object.keys(body).length === 0) {
      toast.message("No changes to save.");
      return;
    }
    updateMutation.mutate({ id: editing.id, body });
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
          onChange={(e) => setSearchInput(e.target.value)}
          placeholder="Search name, slug, or description…"
          className="h-9 pl-8 text-xs bg-input border-border"
          aria-label="Search categories"
        />
      </div>

      {listQuery.isPending && !pageData ? (
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
                <TableHead className="text-xs w-[10%] min-w-[5.5rem] text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={4} className="text-xs text-muted-foreground text-center py-8">
                    {debouncedSearch ? "No categories match this search." : "No categories in the catalog."}
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
                    {row.description ?? "—"}
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
            <DialogTitle>New category</DialogTitle>
            <DialogDescription>Name, optional description, and URL slug.</DialogDescription>
          </DialogHeader>
          <form
            className="space-y-3"
            onSubmit={createForm.handleSubmit((v) => createMutation.mutate(v))}
          >
            <div className="space-y-1.5">
              <Label htmlFor="cat-create-name">Name</Label>
              <Input
                id="cat-create-name"
                {...createForm.register("name")}
                onBlur={(e) => {
                  const slug = createForm.getValues("slug");
                  if (!slug && e.target.value) {
                    createForm.setValue("slug", slugifyHint(e.target.value), { shouldValidate: true });
                  }
                }}
              />
              {createForm.formState.errors.name && (
                <p className="text-xs text-destructive">{createForm.formState.errors.name.message}</p>
              )}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-create-desc">Description</Label>
              <Textarea id="cat-create-desc" className="min-h-[4rem]" {...createForm.register("description")} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-create-slug">Slug</Label>
              <Input id="cat-create-slug" className="font-mono text-xs" {...createForm.register("slug")} />
              {createForm.formState.errors.slug && (
                <p className="text-xs text-destructive">{createForm.formState.errors.slug.message}</p>
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
            <DialogTitle>Edit category</DialogTitle>
            <DialogDescription>Update fields and save.</DialogDescription>
          </DialogHeader>
          <form className="space-y-3" onSubmit={updateForm.handleSubmit(submitUpdate)}>
            <div className="space-y-1.5">
              <Label htmlFor="cat-edit-name">Name</Label>
              <Input id="cat-edit-name" {...updateForm.register("name")} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-edit-desc">Description</Label>
              <Textarea id="cat-edit-desc" className="min-h-[4rem]" {...updateForm.register("description")} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-edit-slug">Slug</Label>
              <Input id="cat-edit-slug" className="font-mono text-xs" {...updateForm.register("slug")} />
              {updateForm.formState.errors.slug && (
                <p className="text-xs text-destructive">{updateForm.formState.errors.slug.message}</p>
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
            <AlertDialogTitle>Delete category?</AlertDialogTitle>
            <AlertDialogDescription>
              {deleting ? `"${deleting.name}" will be removed. Assets still referencing it may break.` : null}
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
