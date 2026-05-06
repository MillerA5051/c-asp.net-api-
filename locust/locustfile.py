from locust import HttpUser, task, between, LoadTestShape
import random

# ============================================================
# USER CLASSES — three modes for your demo
#
# In the Locust web UI at localhost:8089, when you start a test
# you can pick which class to run. Use this to tell a story:
#   1. Start with NormalUser  → stable, green dashboard
#   2. Stop, restart with DegradedUser → yellow, budget draining
#   3. Stop, restart with IncidentUser → red, budget burning fast
#   4. Stop, restart with NormalUser  → recovery, budget refills
# ============================================================


class NormalUser(HttpUser):
    """
    Healthy baseline traffic. Mostly reads, realistic think time.
    Error rate stays well under 0.5% — SLI stays green.
    Burn rate should hover around 0.5-1.0 (sustainable).
    """
    wait_time = between(1, 3)  # real users don't hammer APIs

    @task(6)
    def list_orders(self):
        self.client.get("/api/orders")

    @task(4)
    def get_order(self):
        # IDs 1-90 all succeed — only hitting the happy path
        order_id = random.randint(1, 90)
        self.client.get(f"/api/orders/{order_id}", name="/api/orders/{id}")

    @task(2)
    def create_order(self):
        items = ["Widget", "Gadget", "Doohickey", "Thingamajig"]
        self.client.post("/api/orders", json={
            "item": random.choice(items),
            "quantity": random.randint(1, 5)
        })

    @task(1)
    def health_check(self):
        # Some load balancers and monitoring tools poll /health
        self.client.get("/health")


class DegradedUser(HttpUser):
    """
    Simulates a degraded dependency — like a slow database or
    an upstream service returning errors intermittently.
    Mix of normal traffic + requests that hit bad order IDs (404s)
    + some chaos. Burn rate climbs to 3-5x. Budget draining but
    not yet critical. Dashboard turns yellow.
    """
    wait_time = between(0.5, 1.5)  # users retry faster when things are slow

    @task(4)
    def list_orders(self):
        self.client.get("/api/orders")

    @task(4)
    def get_order_mixed(self):
        # Mix of valid and invalid IDs — generates a stream of 404s
        order_id = random.randint(50, 180)
        self.client.get(
            f"/api/orders/{order_id}",
            name="/api/orders/{id}",
        )

    @task(3)
    def create_order(self):
        # POST has a 5% error rate built in — under load this adds up
        self.client.post("/api/orders", json={
            "item": "Widget",
            "quantity": 1
        })

    @task(2)
    def chaos(self):
        # Some chaos traffic but not overwhelming
        self.client.get("/api/chaos")


class IncidentUser(HttpUser):
    """
    Active incident. A bad deploy or cascading failure.
    Heavy chaos traffic, fast requests (users + automated retries).
    Burn rate spikes to 10-15x. Error budget drains fast.
    Dashboard goes red. This is when you'd get paged.
    """
    wait_time = between(0.1, 0.5)  # retries pile up during incidents

    @task(1)
    def list_orders(self):
        # Even normal endpoints get hammered during incidents
        self.client.get("/api/orders")

    @task(3)
    def bad_orders(self):
        # Mostly hitting IDs that don't exist
        order_id = random.randint(101, 999)
        self.client.get(
            f"/api/orders/{order_id}",
            name="/api/orders/{id}",
        )

    @task(6)
    def chaos(self):
        # Chaos is the dominant traffic type — 40% error rate on this endpoint
        self.client.get("/api/chaos")


# ============================================================
# AUTOMATED DEMO SHAPE (optional)
#
# If you want Locust to automatically cycle through all three
# phases without you having to restart it, uncomment this class
# and comment out the three user classes above. Then in the
# Locust UI just start the test — it runs the story for you.
#
# Phases:
#   0-2 min:  Normal operation (20 users)
#   2-5 min:  Degrading service (35 users, worse behavior)
#   5-8 min:  Full incident   (50 users, chaos heavy)
#   8-10 min: Recovery        (10 users, back to normal)
# ============================================================

# class DemoShape(LoadTestShape):
#     phases = [
#         {"duration": 120, "users": 20,  "spawn_rate": 2,  "user_class": NormalUser},
#         {"duration": 300, "users": 35,  "spawn_rate": 5,  "user_class": DegradedUser},
#         {"duration": 480, "users": 50,  "spawn_rate": 10, "user_class": IncidentUser},
#         {"duration": 600, "users": 10,  "spawn_rate": 2,  "user_class": NormalUser},
#     ]
#
#     def tick(self):
#         run_time = self.get_run_time()
#         for phase in self.phases:
#             if run_time < phase["duration"]:
#                 return (phase["users"], phase["spawn_rate"])
#         return None  # stops the test
