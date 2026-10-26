# صورة Python متعددة المعماريات؛ صورة HA الافتراضية لا تحتوي pip.
FROM python:3.12-alpine
WORKDIR /opt/salary-sync
COPY requirements.txt ./
RUN python -m pip install --no-cache-dir -r requirements.txt
COPY app ./app
RUN mkdir -p /data && chmod 700 /data
CMD ["python", "-m", "app.server"]
