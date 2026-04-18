FROM node:20-alpine AS builder
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .

ARG VITE_API_URL=http://localhost:8080/api
ARG VITE_SPEECH_URL=http://localhost:8000
ENV VITE_API_URL=${VITE_API_URL}
ENV VITE_SPEECH_URL=${VITE_SPEECH_URL}

RUN npm run build

FROM nginx:1.27-alpine AS runtime
WORKDIR /usr/share/nginx/html

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=builder /app/dist ./

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]