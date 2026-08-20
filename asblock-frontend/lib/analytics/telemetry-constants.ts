export const ANALYTICS_SRC_PARAM = 'src'
export const ANALYTICS_COLLECTION_ID_PARAM = 'collectionId'

export const ANALYTICS_EVENT_TYPES = [
  'ASSET_VIEW',
  'BUNDLE_VIEW',
  'COLLECTION_VIEW',
  'COLLECTION_ITEM_CLICK',
  'DOWNLOAD_REQUESTED',
] as const

export type AnalyticsEventType = (typeof ANALYTICS_EVENT_TYPES)[number]

export const ANALYTICS_TRAFFIC_SOURCES = [
  'CATALOG',
  'SEARCH',
  'SELLER_PROFILE',
  'COLLECTION',
  'BUNDLE_PAGE',
  'DIRECT_INTERNAL',
  'EXTERNAL',
  'UNKNOWN',
] as const

export type AnalyticsTrafficSource = (typeof ANALYTICS_TRAFFIC_SOURCES)[number]

export const ANALYTICS_SOURCE_QUERY_VALUES = [
  'catalog',
  'search',
  'seller_profile',
  'collection',
  'bundle_page',
  'direct_internal',
  'external',
  'unknown',
] as const

export type AnalyticsSourceQuery = (typeof ANALYTICS_SOURCE_QUERY_VALUES)[number]

export const ANALYTICS_DEVICE_CLASSES = ['MOBILE', 'TABLET', 'DESKTOP', 'UNKNOWN'] as const

export type AnalyticsDeviceClass = (typeof ANALYTICS_DEVICE_CLASSES)[number]
