import type { MechWareApiClient } from './index';
import { notificationListQuery, type NotificationPage } from './notifications-model';

type Data<T> = { data: T };

/** The signed-in buyer's own in-app notifications. */
export function createNotificationsApi(client: MechWareApiClient) {
  const base = '/api/v1/notifications';
  return {
    list: (unreadOnly: boolean, page = 1) => client.fetch<Data<NotificationPage>>(`${base}?${notificationListQuery(unreadOnly, page)}`).then((r) => r.data),
    unreadCount: () => client.fetch<Data<{ count: number }>>(`${base}/unread-count`).then((r) => r.data.count),
    markRead: (id: string) => client.fetch<unknown>(`${base}/${encodeURIComponent(id)}/read`, { method: 'POST' }).then(() => undefined),
    markAllRead: () => client.fetch<Data<{ updated: number }>>(`${base}/read-all`, { method: 'POST' }).then((r) => r.data.updated)
  };
}
export type NotificationsApi = ReturnType<typeof createNotificationsApi>;
