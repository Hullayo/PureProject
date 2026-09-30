/**
 * 选择本地文件
 *
 * 之前各处都是 `document.createElement('input')` + `input.click()`，
 * **元素从未挂到 DOM 上** —— 在部分 WebView / 浏览器里会被忽略（点了没反应），
 * 而且外部无法观测。这里统一挂到 DOM（视觉上隐藏）再点击，并处理「用户取消」。
 *
 * 注意：文件选择无法用纯前端实现，只能触发系统对话框；因此没有 Tauri/Android 分支。
 *
 * @module utils/pick-file
 */

/**
 * 弹出系统文件选择框
 *
 * @param accept - 接受的文件类型（如 `'.pm,.json'`）
 * @returns 选中的文件；用户取消则返回 null
 */
export function pickFile(accept = ''): Promise<File | null> {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    if (accept) input.accept = accept;
    // 挂进 DOM 但不可见（WebView 里对“游离节点”的 click 支持不一致）
    Object.assign(input.style, {
      position: 'fixed',
      left: '-10000px',
      top: '0',
      width: '1px',
      height: '1px',
      opacity: '0',
      pointerEvents: 'none',
    });
    document.body.appendChild(input);

    let done = false;
    const finish = (file: File | null) => {
      if (done) return;
      done = true;
      window.removeEventListener('focus', onFocus);
      input.remove();
      resolve(file);
    };

    // 用户取消时不会触发 change：借「窗口重新获得焦点」兜底
    const onFocus = () => window.setTimeout(() => finish(input.files?.[0] ?? null), 300);

    input.addEventListener('change', () => finish(input.files?.[0] ?? null), { once: true });
    window.addEventListener('focus', onFocus, { once: true });

    input.click();
  });
}
