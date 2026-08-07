import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import './styles/global.css'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'

const app = createApp(App)
app.use(ElementPlus)
app.use(createPinia())
app.use(router)
// ADR-007 P3-5：模板未使用任何 Element Plus 图标组件（无 el-icon 引用），移除全量注册以减小包体
app.mount('#app')
