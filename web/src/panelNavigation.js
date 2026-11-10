export const panelSections = [
  {
    title: '工作区',
    pages: [
      { path: '/dashboard', title: '运行概览', icon: 'Odometer', description: '掌握消息、模型调用与当前处理状态。' },
      { path: '/chat', title: '聊天记录', icon: 'ChatDotRound', description: '按会话与消息来源，回看每一次交流。' },
      { path: '/ai-stats', title: 'AI 统计', icon: 'DataLine', description: '查看模型请求、已知 Token 用量与使用排行。' }
    ]
  },
  {
    title: '机器人配置',
    pages: [
      { path: '/groups', title: '群管理', icon: 'User', description: '管理每个群的响应方式、人设与群记忆。' },
      { path: '/private-chats', title: '私聊管理', icon: 'ChatLineSquare', description: '管理可使用私聊的用户与管理员权限。' },
      { path: '/proactive', title: '择机发言', icon: 'ChatDotSquare', description: '设置参与讨论的时机，并回看接话结果。' },
      { path: '/ai-settings', title: 'AI 设置', icon: 'Setting', description: '配置模型连接、角色人设、上下文与搜索能力。' }
    ]
  },
  {
    title: '系统',
    pages: [
      { path: '/logs', title: '实时日志', icon: 'Document', description: '跟踪运行事件，筛选与导出需要排查的信息。' },
      { path: '/account', title: '账号中心', icon: 'Postcard', description: '管理登录密码与当前活跃会话。' },
      { path: '/about', title: '关于与设置', icon: 'InfoFilled', description: '了解 Mutter，管理程序更新与面板外观。' }
    ]
  }
]

export const panelPages = panelSections.flatMap(section => section.pages.map(page => ({ ...page, section: section.title })))
