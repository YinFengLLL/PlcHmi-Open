# -*- coding: utf-8 -*-
"""操作按钮图标 —— 叠加在 HMI 触摸按钮上的图形符号"""

SIZE, SW = 24, 1.75

ICONS = {
    "start": {
        "label": "启动",
        "size": SIZE, "sw": SW,
        "els": [
            ("path", "M8.2 5.6v12.8l10-6.4z"),
        ],
    },
    "stop": {
        "label": "停止",
        "size": SIZE, "sw": SW,
        "els": [
            ("rect", 6.8, 6.8, 10.4, 10.4, 1.8),
        ],
    },
    "reset": {
        "label": "复位",
        "size": SIZE, "sw": SW,
        "els": [
            ("path", "M3 12a9 9 0 1 0 3-6.7L3 8"),
            ("path", "M3 3v5h5"),
        ],
    },
    "pause": {
        "label": "暂停",
        "size": SIZE, "sw": SW,
        "els": [
            ("frect", 8.2, 6, 3.1, 12, 1.5),
            ("frect", 12.7, 6, 3.1, 12, 1.5),
        ],
    },
    "estop": {
        "label": "急停",
        "size": SIZE, "sw": SW,
        "els": [
            ("circle", 12, 10.8, 6.6),
            ("dot", 12, 10.8, 3.8),
            ("path", "M12 17.4v3.2"),
            ("line", 9.4, 20.6, 14.6, 20.6),
        ],
    },
    "auto": {
        "label": "自动模式",
        "size": SIZE, "sw": SW,
        "els": [
            ("path", "M17 3.6l3 3-3 3"),
            ("path", "M4 11.6V10a3.4 3.4 0 0 1 3.4-3.4H20"),
            ("path", "M7 20.4l-3-3 3-3"),
            ("path", "M20 12.4V14a3.4 3.4 0 0 1-3.4 3.4H4"),
        ],
    },
    "manual": {
        "label": "手动模式",
        "size": SIZE, "sw": SW,
        "els": [
            ("path", "M8.4 12.6V6.9a1.6 1.6 0 0 1 3.2 0v5.7"),
            ("path", "M11.6 12.6V5.7a1.6 1.6 0 0 1 3.2 0v6.9"),
            ("path", "M14.8 12.9V7.8a1.6 1.6 0 0 1 3.2 0v8.1a5.6 5.6 0 0 1-5.6 5.6h-1.3a4.6 4.6 0 0 1-3.3-1.4l-3.4-3.6a1.6 1.6 0 0 1 2.2-2.2l2.3 2.2"),
        ],
    },
    "forward": {
        "label": "正转 / 前进",
        "size": SIZE, "sw": SW,
        "els": [
            ("path", "M13.4 5.6L19.4 12l-6 6.4"),
            ("line", 5, 12, 19, 12),
        ],
    },
    "reverse": {
        "label": "反转 / 后退",
        "size": SIZE, "sw": SW,
        "els": [
            ("path", "M10.6 5.6L4.6 12l6 6.4"),
            ("line", 5, 12, 19, 12),
        ],
    },
    "home": {
        "label": "回原点",
        "size": SIZE, "sw": SW,
        "els": [
            ("circle", 12, 12, 8),
            ("circle", 12, 12, 3.6),
            ("dot", 12, 12, 1.15),
        ],
    },
}
