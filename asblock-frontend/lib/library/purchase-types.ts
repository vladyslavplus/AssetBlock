export interface PurchaseLibraryItem {
  id: string;
  assetId: string;
  assetTitle: string;
  price: number;
  purchasedAt: string;
  authorUsername: string;
  hasUserReviewed: boolean;
}

export interface PagedPurchaseLibraryDto {
  items: PurchaseLibraryItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}
