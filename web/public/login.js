export function safeReturnPath(value, origin) {
  if (!value || !value.startsWith('/') || /[\\\u0000-\u0020]/u.test(value)) return '/'
  try {
    const target = new URL(value, origin)
    return target.origin === origin && !target.pathname.startsWith('//')
      ? target.pathname + target.search + target.hash : '/'
  } catch { return '/' }
}

function initializeLogin() {
'use strict';

var params = new URLSearchParams(window.location.search);
var returnUrl = safeReturnPath(params.get('return'), window.location.origin);

var form = document.getElementById('loginForm');
var changeForm = document.getElementById('changeForm');
var noticeBox = document.getElementById('notice');
var alertBox = document.getElementById('alert');
var alertText = document.getElementById('alertText');
var submitBtn = document.getElementById('submitBtn');
var submitLabel = document.getElementById('submitLabel');
var changeBtn = document.getElementById('changeBtn');
var changeLabel = document.getElementById('changeLabel');
var formTitle = document.getElementById('formTitle');
var formSub = document.getElementById('formSub');
var usernameInput = document.getElementById('username');
var passwordInput = document.getElementById('password');
var currentPasswordInput = document.getElementById('currentPassword');
var bootstrapTokenInput = document.getElementById('bootstrapToken');
var newPasswordInput = document.getElementById('newPassword');
var confirmPasswordInput = document.getElementById('confirmPassword');
var toggleBtn = document.getElementById('togglePassword');
var iconEye = document.getElementById('iconEye');
var iconEyeOff = document.getElementById('iconEyeOff');

var submitting = false;
var changing = false;

function setInvalid(input, invalid) {
  if (invalid) input.setAttribute('aria-invalid', 'true');
  else input.removeAttribute('aria-invalid');
}

function showAlert(message, success) {
  alertText.textContent = message;
  alertBox.hidden = false;
  alertBox.classList.toggle('is-success', !!success);
  alertBox.classList.remove('shake');
  if (success) return;
  // 强制重排，让同一条错误再次出现时也能重新抖动
  void alertBox.offsetWidth;
  alertBox.classList.add('shake');
}

function clearAlert() {
  if (alertBox.hidden) return;
  alertBox.hidden = true;
  alertBox.classList.remove('shake');
  alertBox.classList.remove('is-success');
  alertText.textContent = '';
  [usernameInput, passwordInput, currentPasswordInput, bootstrapTokenInput, newPasswordInput, confirmPasswordInput]
    .forEach(function (input) { setInvalid(input, false); });
}

function setLoading(loading) {
  submitting = loading;
  submitBtn.disabled = loading;
  submitBtn.classList.toggle('is-loading', loading);
  submitBtn.setAttribute('aria-busy', loading ? 'true' : 'false');
  submitLabel.textContent = loading ? '登录中…' : '登 录';
  usernameInput.readOnly = loading;
  passwordInput.readOnly = loading;
}

function setChangeLoading(loading) {
  changing = loading;
  changeBtn.disabled = loading;
  changeBtn.classList.toggle('is-loading', loading);
  changeBtn.setAttribute('aria-busy', loading ? 'true' : 'false');
  changeLabel.textContent = loading ? '提交中…' : '修改密码';
  currentPasswordInput.readOnly = loading;
  bootstrapTokenInput.readOnly = loading;
  newPasswordInput.readOnly = loading;
  confirmPasswordInput.readOnly = loading;
}

// 初始默认密码命中：切换到强制改密表单
function showChangeForm(currentPassword) {
  setLoading(false);
  clearAlert();
  form.style.display = 'none';
  changeForm.style.display = '';
  noticeBox.classList.add('show');
  formTitle.textContent = '设置新密码';
  formSub.textContent = '初始密码不能继续使用，请先设置一个新的管理员密码。';
  currentPasswordInput.value = currentPassword || '';
  bootstrapTokenInput.value = '';
  newPasswordInput.value = '';
  confirmPasswordInput.value = '';
  newPasswordInput.focus();
}

function showLoginForm() {
  changeForm.style.display = 'none';
  noticeBox.classList.remove('show');
  form.style.display = '';
  formTitle.textContent = '欢迎回来';
  formSub.textContent = '登录以继续管理你的 Mutter 机器人。';
  passwordInput.value = '';
  currentPasswordInput.value = '';
  bootstrapTokenInput.value = '';
  newPasswordInput.value = '';
  confirmPasswordInput.value = '';
  passwordInput.focus();
}

toggleBtn.addEventListener('click', function () {
  var visible = passwordInput.type === 'password';
  passwordInput.type = visible ? 'text' : 'password';
  toggleBtn.setAttribute('aria-pressed', visible ? 'true' : 'false');
  toggleBtn.setAttribute('aria-label', visible ? '隐藏密码' : '显示密码');
  iconEye.hidden = visible;
  iconEyeOff.hidden = !visible;
  passwordInput.focus({ preventScroll: true });
  var end = passwordInput.value.length;
  try { passwordInput.setSelectionRange(end, end); } catch (e) { /* 忽略 */ }
});

[usernameInput, passwordInput, currentPasswordInput, bootstrapTokenInput, newPasswordInput, confirmPasswordInput]
  .forEach(function (input) {
    input.addEventListener('input', function () {
      if (!alertBox.hidden) clearAlert();
      setInvalid(input, false);
    });
  });

form.addEventListener('submit', function (event) {
  event.preventDefault();
  if (submitting) return;

  clearAlert();

  var username = usernameInput.value.trim();
  var password = passwordInput.value;

  if (!username || !password) {
    showAlert('请输入账号和密码');
    if (!username) {
      setInvalid(usernameInput, true);
      usernameInput.focus();
    } else {
      setInvalid(passwordInput, true);
      passwordInput.focus();
    }
    return;
  }

  setLoading(true);

  fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Mutter-Request': '1' },
    credentials: 'same-origin',
    body: JSON.stringify({ username: username, password: password })
  })
    .then(function (response) {
      return response.json()
        .catch(function () { return null; })
        .then(function (data) {
          return { ok: response.ok, data: data };
        });
    })
    .then(function (result) {
      if (result.ok) {
        window.location.href = returnUrl;
        return;
      }
      if (result.data && result.data.mustChangePassword) {
        showChangeForm(password);
        return;
      }
      throw new Error((result.data && result.data.error) || '登录失败');
    })
    .catch(function (error) {
      var message = error && error.message ? error.message : '登录失败，请重试';
      if (error instanceof TypeError) message = '无法连接服务器，请检查网络后重试';
      showAlert(message);
      setLoading(false);
      passwordInput.focus({ preventScroll: true });
      var end = passwordInput.value.length;
      try { passwordInput.setSelectionRange(end, end); } catch (e) { /* 忽略 */ }
    });
});

changeForm.addEventListener('submit', function (event) {
  event.preventDefault();
  if (changing) return;

  clearAlert();

  var currentPassword = currentPasswordInput.value;
  var bootstrapToken = bootstrapTokenInput.value.trim();
  var newPassword = newPasswordInput.value;
  var confirmPassword = confirmPasswordInput.value;

  if (!currentPassword || !newPassword || !confirmPassword) {
    showAlert('请填写完整的修改密码信息');
    return;
  }
  if (!/^[0-9a-fA-F]{32}$/.test(bootstrapToken)) {
    showAlert('请填写服务器维护文件中的 32 位初始化码');
    setInvalid(bootstrapTokenInput, true);
    bootstrapTokenInput.focus();
    return;
  }
  if (newPassword.length < 15 || newPassword.length > 128) {
    showAlert('新密码须为 15 至 128 位');
    setInvalid(newPasswordInput, true);
    newPasswordInput.focus();
    return;
  }
  if (newPassword !== confirmPassword) {
    showAlert('两次输入的新密码不一致');
    setInvalid(confirmPasswordInput, true);
    confirmPasswordInput.focus();
    return;
  }

  setChangeLoading(true);

  fetch('/api/auth/change-password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Mutter-Request': '1' },
    credentials: 'same-origin',
    body: JSON.stringify({
      currentPassword: currentPassword,
      bootstrapToken: bootstrapToken,
      newPassword: newPassword,
      confirmPassword: confirmPassword
    })
  })
    .then(function (response) {
      return response.json()
        .catch(function () { return null; })
        .then(function (data) {
          if (!response.ok) {
            throw new Error((data && data.error) || '修改密码失败');
          }
          return data;
        });
    })
    .then(function (data) {
      setChangeLoading(false);
      showLoginForm();
      showAlert((data && data.message) || '密码修改成功，请使用新密码登录', true);
    })
    .catch(function (error) {
      var message = error && error.message ? error.message : '修改密码失败，请重试';
      if (error instanceof TypeError) message = '无法连接服务器，请检查网络后重试';
      showAlert(message);
      setChangeLoading(false);
      currentPasswordInput.focus();
    });
});
}

if (typeof document !== 'undefined') initializeLogin()
