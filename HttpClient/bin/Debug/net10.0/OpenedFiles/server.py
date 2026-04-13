import http.server
import socketserver
import json
import subprocess
import sys
from urllib.parse import urlparse, parse_qs

PORT = 8000

HTML = """<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>Лабораторная работа №3</title>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        h1 { color: #333; }
        .tabs { display: flex; gap: 5px; margin-bottom: 20px; }
        .tab { padding: 10px 20px; background: #f0f0f0; border: 1px solid #ccc; cursor: pointer; }
        .tab.active { background: #007BFF; color: white; }
        .pane { display: none; border: 1px solid #ccc; padding: 20px; border-radius: 5px; }
        .pane.active { display: block; }
        label { display: block; margin: 10px 0 5px; }
        input, textarea { width: 100%; padding: 8px; box-sizing: border-box; }
        button { margin: 15px 0; padding: 10px 20px; background: #007BFF; color: white; border: none; cursor: pointer; }
        pre { background: #f8f8f8; padding: 10px; border-radius: 5px; overflow: auto; }
    </style>
</head>
<body>
    <h1>Лабораторная работа №3: Типичные операции</h1>
    <div class="tabs">
        <div class="tab active" onclick="showTab(1)">Задание 1</div>
        <div class="tab" onclick="showTab(2)">Задание 2</div>
        <div class="tab" onclick="showTab(3)">Задание 3</div>
        <div class="tab" onclick="showTab(4)">Задание 4</div>
        <div class="tab" onclick="showTab(5)">Задание 5</div>
    </div>

    <div id="pane1" class="pane active">
        <h2>Обработка списка городов</h2>
        <label>Введите города через запятую:</label>
        <textarea id="input1">Москва, Санкт-Петербург, Москва, Казань, Новосибирск</textarea>
        <button onclick="submitTask(1)">Выполнить</button>
        <pre id="result1"></pre>
    </div>

    <div id="pane2" class="pane">
        <h2>Обработка текста</h2>
        <label>Введите текст:</label>
        <textarea id="input2">Это пример текста для обработки</textarea>
        <button onclick="submitTask(2)">Выполнить</button>
        <div id="result2"></div>
    </div>

    <div id="pane3" class="pane">
        <h2>Объединение наборов чисел</h2>
        <label>Первый набор (через пробел):</label>
        <input id="input3_1" value="2 2 5 3 7 2">
        <label>Второй набор (через пробел):</label>
        <input id="input3_2" value="2 4 4 85">
        <button onclick="submitTask(3)">Выполнить</button>
        <pre id="result3"></pre>
    </div>

    <div id="pane4" class="pane">
        <h2>Перестановка слов</h2>
        <label>Введите строку:</label>
        <input id="input4" value="раз два три четыре пять">
        <button onclick="submitTask(4)">Выполнить</button>
        <pre id="result4"></pre>
    </div>

    <div id="pane5" class="pane">
        <h2>Обработка многомерного массива</h2>
        <label>Введите массив в формате JSON:</label>
        <textarea id="input5">{"nums": [1, 2.5, 3], "str": "hello", "nested": {"a": 1, "b": 2.333}}</textarea>
        <button onclick="submitTask(5)">Выполнить</button>
        <pre id="result5"></pre>
    </div>

    <script>
        function showTab(n) {
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.pane').forEach(p => p.classList.remove('active'));
            document.querySelectorAll('.tab')[n-1].classList.add('active');
            document.getElementById('pane'+n).classList.add('active');
        }

        async function submitTask(n) {
            let url = `/task${n}`;
            let data = {};
            const method = (n === 1 || n === 2) ? 'GET' : 'POST';

            if (n === 1) {
                let text = document.getElementById('input1').value;
                let cities = text.split(',').map(s => s.trim());
                url += `?cities=${encodeURIComponent(JSON.stringify(cities))}`;
            } else if (n === 2) {
                let text = document.getElementById('input2').value;
                url += `?text=${encodeURIComponent(text)}`;
            } else if (n === 3) {
                let s1 = document.getElementById('input3_1').value;
                let s2 = document.getElementById('input3_2').value;
                data.set1 = s1.split(/\\s+/).map(Number);
                data.set2 = s2.split(/\\s+/).map(Number);
            } else if (n === 4) {
                data.text = document.getElementById('input4').value;
            } else if (n === 5) {
                try {
                    data.array = JSON.parse(document.getElementById('input5').value);
                } catch (e) {
                    alert('Некорректный JSON');
                    return;
                }
            }

            try {
                const response = await fetch(url, {
                    method: method,
                    headers: method === 'POST' ? {'Content-Type': 'application/json'} : {},
                    body: method === 'POST' ? JSON.stringify(data) : undefined
                });
                const result = await response.json();
                const pre = document.getElementById(`result${n}`);
                if (n === 2 && result.colored_html) {
                    pre.innerHTML = '<div><b>Исходный:</b> ' + result.original + '</div>' +
                                    '<div><b>Обработанный (слова):</b> ' + result.processed_text + '</div>' +
                                    '<div><b>С цветом:</b> ' + result.colored_html + '</div>';
                } else {
                    pre.textContent = JSON.stringify(result, null, 2);
                }
            } catch (err) {
                document.getElementById(`result${n}`).textContent = 'Ошибка: ' + err;
            }
        }
    </script>
</body>
</html>
"""


class Handler(http.server.SimpleHTTPRequestHandler):
    def run_script(self, script_name, data):
        proc = subprocess.Popen(
            [sys.executable, script_name],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        out, err = proc.communicate(input=json.dumps(data, ensure_ascii=False))
        if proc.returncode != 0:
            raise Exception(f'Script error: {err}')
        return json.loads(out)

    def do_GET(self):
        if self.path == '/':
            self.send_response(200)
            self.send_header('Content-type', 'text/html; charset=utf-8')
            self.end_headers()
            self.wfile.write(HTML.encode('utf-8'))
            return

        parsed = urlparse(self.path)
        params = parse_qs(parsed.query)

        if parsed.path == '/task1' and 'cities' in params:
            try:
                cities = json.loads(params['cities'][0])
                data = {'cities': cities}
                result = self.run_script('task1.py', data)
                self.send_response(200)
                self.send_header('Content-type', 'application/json; charset=utf-8')
                self.end_headers()
                self.wfile.write(json.dumps(result, ensure_ascii=False).encode('utf-8'))
            except Exception as e:
                self.send_response(500)
                self.end_headers()
                self.wfile.write(str(e).encode('utf-8'))

        elif parsed.path == '/task2' and 'text' in params:
            try:
                text = params['text'][0]
                data = {'text': text}
                result = self.run_script('task2.py', data)
                self.send_response(200)
                self.send_header('Content-type', 'application/json; charset=utf-8')
                self.end_headers()
                self.wfile.write(json.dumps(result, ensure_ascii=False).encode('utf-8'))
            except Exception as e:
                self.send_response(500)
                self.end_headers()
                self.wfile.write(str(e).encode('utf-8'))

        else:
            self.send_response(404)
            self.end_headers()
            self.wfile.write(b'Not found')

    def do_POST(self):
        if not self.path.startswith('/task'):
            self.send_response(404)
            self.end_headers()
            return

        task_num = self.path[5:]
        if not task_num.isdigit() or int(task_num) not in range(1, 6):
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b'Invalid task number')
            return

        if task_num in ['1', '2']:
            self.send_response(405)
            self.end_headers()
            self.wfile.write(b'Use GET for this task')
            return

        content_length = int(self.headers.get('Content-Length', 0))
        post_data = self.rfile.read(content_length)
        try:
            data = json.loads(post_data.decode('utf-8'))
        except:
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b'Invalid JSON')
            return

        script_name = f'task{task_num}.py'
        try:
            result = self.run_script(script_name, data)
        except Exception as e:
            self.send_response(500)
            self.end_headers()
            self.wfile.write(str(e).encode('utf-8'))
            return

        self.send_response(200)
        self.send_header('Content-type', 'application/json; charset=utf-8')
        self.end_headers()
        self.wfile.write(json.dumps(result, ensure_ascii=False).encode('utf-8'))


if __name__ == '__main__':
    with socketserver.TCPServer(("", PORT), Handler) as httpd:
        print(f"Сервер запущен на http://localhost:{PORT}")
        httpd.serve_forever()