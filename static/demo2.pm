{
  "version": "1.0",
  "project": {
    "name": "Web Dashboard 重构",
    "description": "将旧版 jQuery 管理后台迁移到 React + TypeScript 架构",
    "template": "web",
    "color": "#06b6d4",
    "created_at": "2026-05-25T08:00:00.000Z",
    "updated_at": "2026-06-01T10:00:00.000Z",
    "start_date": "2026-05-25",
    "end_date": "2026-06-20",
    "default_task_group_id": "group-dc6cef95"
  },
  "readme_file": "README.md",
  "template": {
    "dirs": [
      "src",
      "public",
      "tests"
    ],
    "files": [
      "README.md",
      "package.json",
      "tsconfig.json"
    ],
    "file_contents": {
      "README.md": "# Web Dashboard 重构\n\n旧版 jQuery → React + TypeScript",
      "package.json": "{}",
      "tsconfig.json": "{}"
    }
  },
  "tasks": [
    {
      "id": "w1",
      "title": "搭建 React 脚手架",
      "description": "使用 Vite 创建 React + TypeScript 项目",
      "priority": "high",
      "tags": [
        "前端"
      ],
      "due_date": "2026-05-28",
      "start_offset": 0,
      "dependencies": [],
      "created_at": "2026-05-25T08:00:00.000Z",
      "updated_at": "2026-05-27T18:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-dc6cef95",
      "status_id": "status-d9e29d9b",
      "completed_at": "2026-05-27T18:00:00.000Z"
    },
    {
      "id": "w2",
      "title": "用户管理模块",
      "description": "用户列表、角色管理、权限控制组件",
      "priority": "high",
      "tags": [
        "前端"
      ],
      "due_date": "2026-06-02",
      "start_offset": 0,
      "dependencies": [
        {
          "taskId": "w1",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-27T08:00:00.000Z",
      "updated_at": "2026-06-01T16:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-dc6cef95",
      "status_id": "status-d9e29d9b",
      "completed_at": "2026-06-01T16:00:00.000Z"
    },
    {
      "id": "w3",
      "title": "数据看板 API 对接",
      "description": "对接后端 REST API，实现图表数据加载",
      "priority": "medium",
      "tags": [
        "API"
      ],
      "due_date": "2026-06-08",
      "start_offset": null,
      "dependencies": [],
      "created_at": "2026-05-28T10:00:00.000Z",
      "updated_at": "2026-06-01T09:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-dc6cef95",
      "status_id": "status-5a86effa",
      "completed_at": null
    },
    {
      "id": "w4",
      "title": "响应式布局适配",
      "description": "确保所有页面在移动端和桌面端正常显示",
      "priority": "medium",
      "tags": [
        "CSS"
      ],
      "due_date": "2026-06-12",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "w2",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-25T08:00:00.000Z",
      "updated_at": "2026-05-25T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-dc6cef95",
      "status_id": "status-c979955f",
      "completed_at": null
    },
    {
      "id": "w5",
      "title": "E2E 测试编写",
      "description": "使用 Playwright 编写核心流程的端到端测试",
      "priority": "low",
      "tags": [
        "测试"
      ],
      "due_date": "2026-06-18",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "w3",
          "dayOffset": 0
        },
        {
          "taskId": "w4",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-25T08:00:00.000Z",
      "updated_at": "2026-05-25T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-dc6cef95",
      "status_id": "status-c979955f",
      "completed_at": null
    },
    {
      "id": "w6",
      "title": "部署上线",
      "description": "构建生产版本，部署到服务器",
      "priority": "high",
      "tags": [
        "部署"
      ],
      "due_date": "2026-06-20",
      "start_offset": null,
      "dependencies": [
        {
          "taskId": "w5",
          "dayOffset": 0
        }
      ],
      "created_at": "2026-05-25T08:00:00.000Z",
      "updated_at": "2026-05-25T08:00:00.000Z",
      "subtasks": [],
      "comments": [],
      "due_time": null,
      "tracked_start": null,
      "reminder": null,
      "recurrence": null,
      "task_group_id": "group-dc6cef95",
      "status_id": "status-c979955f",
      "completed_at": null
    }
  ],
  "tags": [
    {
      "id": "tw1",
      "name": "前端",
      "color": "#3b82f6"
    },
    {
      "id": "tw2",
      "name": "API",
      "color": "#8b5cf6"
    },
    {
      "id": "tw3",
      "name": "CSS",
      "color": "#ec4899"
    },
    {
      "id": "tw4",
      "name": "测试",
      "color": "#10b981"
    },
    {
      "id": "tw5",
      "name": "部署",
      "color": "#f59e0b"
    }
  ],
  "changelog": [
    {
      "version": "1.0.0",
      "date": "2026-05-25",
      "info": "项目启动"
    },
    {
      "version": "1.1.0",
      "date": "2026-06-01",
      "info": "用户管理模块完成"
    }
  ],
  "milestones": [
    {
      "id": "mw1",
      "title": "核心模块完成",
      "date": "2026-06-08",
      "color": "#3b82f6",
      "description": "用户管理、数据看板功能就绪"
    },
    {
      "id": "mw2",
      "title": "上线发布",
      "date": "2026-06-20",
      "color": "#10b981",
      "description": "生产环境部署"
    }
  ],
  "schema_version": 4,
  "task_groups": [
    {
      "id": "group-dc6cef95",
      "name": "默认任务组",
      "sort_order": 1024,
      "archived": false,
      "initial_status_id": "status-c979955f",
      "completion_status_id": "status-d9e29d9b",
      "statuses": [
        {
          "id": "status-d9e29d9b",
          "name": "已完成",
          "color": "#10b981",
          "category": "done",
          "sort_order": 1024
        },
        {
          "id": "status-5a86effa",
          "name": "进行中",
          "color": "#4f46e5",
          "category": "active",
          "sort_order": 2048
        },
        {
          "id": "status-c979955f",
          "name": "待办",
          "color": "#6b7280",
          "category": "todo",
          "sort_order": 3072
        }
      ]
    }
  ]
}
