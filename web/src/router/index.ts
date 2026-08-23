import { createRouter, createWebHistory } from 'vue-router'
import { loadMe, saveMe, getMe } from '../api/user'

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
    { path: '/alarmrules', name: 'AlarmRules', component: () => import('../views/Alarms/AlarmRulesView.vue') },
    { path: '/audit', name: 'AuditLog', component: () => import('../views/Audit/AuditLogView.vue') },
    // ADR-066：用户管理页（仅 Admin；前端门控只是 UX，后端 AdminOnly 策略兜底）
    { path: '/users', name: 'Users', component: () => import('../views/Users/UserListView.vue'), meta: { roles: ['Admin'] } },
    { path: '/system', name: 'SystemStatus', component: () => import('../views/System/SystemStatus.vue') },
    { path: '/history', name: 'History', component: () => import('../views/History/HistoryView.vue') },
  ]
})

// 导航守卫：未登录跳 /login；meta.roles 时校验当前用户角色（前端 UX，后端策略兜底）
router.beforeEach(async (to, from, next) => {
  const token = localStorage.getItem('token')
  if (to.path !== '/login' && !token) {
    next('/login')
  } else if (to.path === '/login' && token) {
    next('/dashboard')
  } else if (to.meta.roles) {
    // 缓存无角色信息时（旧会话）尝试实时拉取 /api/user/me，避免菜单误锁
    const roles = to.meta.roles as string[] | undefined
    let user = loadMe()
    if (!user) {
      try {
        const me = await getMe()
        if (me) { user = me; saveMe(me) }
      } catch { /* 拉取失败按无权限处理，后端会兜底 */ }
    }
    if (!user || !roles?.includes(user.role)) next('/dashboard')
    else next()
  } else {
    next()
  }
})

export default router
