import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'Login', component: () => import('../views/Login/LoginView.vue') },
    { path: '/', redirect: '/dashboard' },
    { path: '/dashboard', name: 'Dashboard', component: () => import('../views/Dashboard/DashboardView.vue') },
    { path: '/devices', name: 'Devices', component: () => import('../views/Devices/DeviceListView.vue') },
    { path: '/devices/new', name: 'DeviceNew', component: () => import('../views/Devices/DeviceForm.vue') },
    { path: '/devices/:id', name: 'DeviceDetail', component: () => import('../views/Devices/DeviceDetailView.vue') },
    { path: '/devices/:id/edit', name: 'DeviceEdit', component: () => import('../views/Devices/DeviceForm.vue') },
    { path: '/devices/:deviceId/points', name: 'Points', component: () => import('../views/Points/PointList.vue') },
    { path: '/monitoring', name: 'Monitoring', component: () => import('../views/Monitoring/MonitoringView.vue') },
    { path: '/alarms', name: 'Alarms', component: () => import('../views/Alarms/AlarmListView.vue') },
    { path: '/deadletters', name: 'DeadLetters', component: () => import('../views/DeadLetters/DeadLettersView.vue') },
    { path: '/system', name: 'SystemStatus', component: () => import('../views/System/SystemStatus.vue') },
    { path: '/history', name: 'History', component: () => import('../views/History/HistoryView.vue') },
  ]
})

// 导航守卫：未登录跳 /login
router.beforeEach((to, from, next) => {
  const token = localStorage.getItem('token')
  if (to.path !== '/login' && !token) {
    next('/login')
  } else if (to.path === '/login' && token) {
    next('/dashboard')
  } else {
    next()
  }
})

export default router
