import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/dashboard' },
    { path: '/dashboard', name: 'Dashboard', component: () => import('../views/Dashboard/DashboardView.vue') },
    { path: '/devices', name: 'Devices', component: () => import('../views/Devices/DeviceListView.vue') },
    { path: '/devices/new', name: 'DeviceNew', component: () => import('../views/Devices/DeviceForm.vue') },
    { path: '/devices/:id', name: 'DeviceDetail', component: () => import('../views/Devices/DeviceDetailView.vue') },
    { path: '/devices/:id/edit', name: 'DeviceEdit', component: () => import('../views/Devices/DeviceForm.vue') },
    { path: '/devices/:deviceId/points', name: 'Points', component: () => import('../views/Points/PointList.vue') },
    { path: '/monitoring', name: 'Monitoring', component: () => import('../views/Monitoring/MonitoringView.vue') },
    { path: '/history', name: 'History', component: () => import('../views/History/HistoryView.vue') },
  ]
})

export default router
