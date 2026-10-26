ARG BUILD_FROM
FROM ${BUILD_FROM}
WORKDIR /opt/salary-sync
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY app ./app
RUN mkdir -p /data && chmod 700 /data
CMD ["python", "-m", "app.server"]
